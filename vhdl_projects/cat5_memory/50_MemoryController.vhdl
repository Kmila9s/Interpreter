-- ============================================================================
-- VHDL Project: MemoryController
-- Category: Memory Structures
-- Complexity: High
-- Description: SDRAM-style memory controller with refresh
--              Command scheduling, bank management, timing control
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity MemoryController is
    generic (
        DATA_WIDTH   : integer := 16;
        ADDR_WIDTH   : integer := 24;  -- 16MB address space
        ROW_WIDTH    : integer := 13;
        COL_WIDTH    : integer := 9;
        BANK_WIDTH   : integer := 2
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- User interface
        user_addr       : in  bit_vector(ADDR_WIDTH-1 downto 0);
        user_data_in    : in  bit_vector(DATA_WIDTH-1 downto 0);
        user_data_out   : out bit_vector(DATA_WIDTH-1 downto 0);
        user_read       : in  bit;
        user_write      : in  bit;
        user_ready      : out bit;
        user_valid      : out bit;
        -- Memory interface (SDRAM-like)
        mem_clk         : out bit;
        mem_cke         : out bit;  -- Clock enable
        mem_cs_n        : out bit;  -- Chip select
        mem_ras_n       : out bit;  -- Row address strobe
        mem_cas_n       : out bit;  -- Column address strobe
        mem_we_n        : out bit;  -- Write enable
        mem_addr        : out bit_vector(ROW_WIDTH-1 downto 0);
        mem_bank        : out bit_vector(BANK_WIDTH-1 downto 0);
        mem_dq_out      : out bit_vector(DATA_WIDTH-1 downto 0);
        mem_dq_in       : in  bit_vector(DATA_WIDTH-1 downto 0);
        mem_dq_oe       : out bit   -- Data bus output enable
    );
end MemoryController;

architecture Behavioral of MemoryController is
    -- SDRAM commands (active low signals)
    -- CS RAS CAS WE
    constant CMD_NOP       : bit_vector(3 downto 0) := "0111";
    constant CMD_ACTIVE    : bit_vector(3 downto 0) := "0011";
    constant CMD_READ      : bit_vector(3 downto 0) := "0101";
    constant CMD_WRITE     : bit_vector(3 downto 0) := "0100";
    constant CMD_PRECHARGE : bit_vector(3 downto 0) := "0010";
    constant CMD_REFRESH   : bit_vector(3 downto 0) := "0001";
    constant CMD_MODE_SET  : bit_vector(3 downto 0) := "0000";

    -- Timing parameters (in clock cycles)
    constant tRCD : integer := 3;  -- RAS to CAS delay
    constant tRP  : integer := 3;  -- Precharge time
    constant tRFC : integer := 10; -- Refresh cycle time
    constant tWR  : integer := 2;  -- Write recovery
    constant CAS_LATENCY : integer := 3;
    constant REFRESH_INTERVAL : integer := 780;  -- ~7.8us at 100MHz

    -- State machine
    type state_type is (
        INIT_WAIT,
        INIT_PRECHARGE,
        INIT_REFRESH1,
        INIT_REFRESH2,
        INIT_MODE_SET,
        IDLE,
        ACTIVE,
        READ_CMD,
        READ_WAIT,
        READ_DATA,
        WRITE_CMD,
        WRITE_DATA,
        PRECHARGE,
        REFRESH
    );
    signal state : state_type;

    -- Address decoding
    signal row_addr : bit_vector(ROW_WIDTH-1 downto 0);
    signal col_addr : bit_vector(COL_WIDTH-1 downto 0);
    signal bank_addr : bit_vector(BANK_WIDTH-1 downto 0);

    -- Bank state tracking
    type bank_state_type is (BANK_IDLE, BANK_ACTIVE);
    type bank_states_array is array (0 to 3) of bank_state_type;
    type bank_rows_array is array (0 to 3) of bit_vector(ROW_WIDTH-1 downto 0);

    signal bank_states : bank_states_array;
    signal active_rows : bank_rows_array;

    -- Timing counters
    signal timer : integer range 0 to 63;
    signal refresh_counter : integer range 0 to REFRESH_INTERVAL;
    signal init_counter : integer range 0 to 255;

    -- Latched request
    signal req_addr : bit_vector(ADDR_WIDTH-1 downto 0);
    signal req_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal req_write : bit;
    signal req_pending : bit;

    -- Data pipeline
    signal read_pending : integer range 0 to 7;

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then result := result + power; end if;
            power := power * 2;
        end loop;
        return result;
    end function;

    function rows_match(a, b : bit_vector) return bit is
    begin
        for i in a'range loop
            if a(i) /= b(i) then return '0'; end if;
        end loop;
        return '1';
    end function;

    procedure issue_command(cmd : in bit_vector(3 downto 0);
                           signal cs, ras, cas, we : out bit) is
    begin
        cs <= cmd(3);
        ras <= cmd(2);
        cas <= cmd(1);
        we <= cmd(0);
    end procedure;

begin
    -- Address breakdown
    bank_addr <= user_addr(ADDR_WIDTH-1 downto ADDR_WIDTH-BANK_WIDTH);
    row_addr <= user_addr(ADDR_WIDTH-BANK_WIDTH-1 downto COL_WIDTH);
    col_addr <= user_addr(COL_WIDTH-1 downto 0);

    -- Clock output
    mem_clk <= clk;

    -- Main controller FSM
    controller_fsm: process(clk, reset)
        variable bank_idx : integer;
        variable need_precharge : bit;
        variable row_hit : bit;
    begin
        if reset = '1' then
            state <= INIT_WAIT;
            timer <= 0;
            init_counter <= 0;
            refresh_counter <= 0;
            req_pending <= '0';
            read_pending <= 0;
            user_ready <= '0';
            user_valid <= '0';
            mem_cke <= '1';
            mem_cs_n <= '1';
            mem_ras_n <= '1';
            mem_cas_n <= '1';
            mem_we_n <= '1';
            mem_dq_oe <= '0';
            for i in 0 to 3 loop
                bank_states(i) <= BANK_IDLE;
            end loop;
        elsif clk'event and clk = '1' then
            -- Default: NOP
            mem_cs_n <= '0';
            mem_ras_n <= '1';
            mem_cas_n <= '1';
            mem_we_n <= '1';
            mem_dq_oe <= '0';
            user_valid <= '0';

            -- Refresh counter
            if refresh_counter < REFRESH_INTERVAL then
                refresh_counter <= refresh_counter + 1;
            end if;

            -- Timer countdown
            if timer > 0 then
                timer <= timer - 1;
            end if;

            -- Read pipeline
            if read_pending > 0 then
                read_pending <= read_pending - 1;
                if read_pending = 1 then
                    user_data_out <= mem_dq_in;
                    user_valid <= '1';
                end if;
            end if;

            case state is
                when INIT_WAIT =>
                    user_ready <= '0';
                    init_counter <= init_counter + 1;
                    if init_counter >= 200 then
                        state <= INIT_PRECHARGE;
                    end if;

                when INIT_PRECHARGE =>
                    -- Precharge all banks
                    mem_ras_n <= '0';
                    mem_we_n <= '0';
                    mem_addr(10) <= '1';  -- All banks
                    timer <= tRP;
                    state <= INIT_REFRESH1;

                when INIT_REFRESH1 =>
                    if timer = 0 then
                        mem_ras_n <= '0';
                        mem_cas_n <= '0';
                        timer <= tRFC;
                        state <= INIT_REFRESH2;
                    end if;

                when INIT_REFRESH2 =>
                    if timer = 0 then
                        mem_ras_n <= '0';
                        mem_cas_n <= '0';
                        timer <= tRFC;
                        state <= INIT_MODE_SET;
                    end if;

                when INIT_MODE_SET =>
                    if timer = 0 then
                        mem_ras_n <= '0';
                        mem_cas_n <= '0';
                        mem_we_n <= '0';
                        -- Mode register: CAS latency, burst length
                        mem_addr <= (others => '0');
                        mem_addr(6 downto 4) <= "011";  -- CAS = 3
                        timer <= 2;
                        state <= IDLE;
                    end if;

                when IDLE =>
                    user_ready <= '1';

                    -- Check for refresh needed
                    if refresh_counter >= REFRESH_INTERVAL then
                        state <= REFRESH;
                        user_ready <= '0';
                    -- Check for new request
                    elsif user_read = '1' or user_write = '1' then
                        req_addr <= user_addr;
                        req_data <= user_data_in;
                        req_write <= user_write;
                        user_ready <= '0';

                        bank_idx := to_int(bank_addr);
                        if bank_states(bank_idx) = BANK_ACTIVE then
                            if rows_match(active_rows(bank_idx), row_addr) = '1' then
                                -- Row hit
                                if user_write = '1' then
                                    state <= WRITE_CMD;
                                else
                                    state <= READ_CMD;
                                end if;
                            else
                                -- Row miss - need precharge
                                state <= PRECHARGE;
                            end if;
                        else
                            -- Bank idle - need activate
                            state <= ACTIVE;
                        end if;
                    end if;

                when ACTIVE =>
                    -- Activate row
                    mem_ras_n <= '0';
                    mem_addr <= row_addr;
                    mem_bank <= bank_addr;
                    bank_idx := to_int(bank_addr);
                    bank_states(bank_idx) <= BANK_ACTIVE;
                    active_rows(bank_idx) <= row_addr;
                    timer <= tRCD;

                    if req_write = '1' then
                        state <= WRITE_CMD;
                    else
                        state <= READ_CMD;
                    end if;

                when READ_CMD =>
                    if timer = 0 then
                        mem_cas_n <= '0';
                        mem_addr(COL_WIDTH-1 downto 0) <= col_addr;
                        mem_bank <= bank_addr;
                        read_pending <= CAS_LATENCY;
                        state <= IDLE;
                    end if;

                when WRITE_CMD =>
                    if timer = 0 then
                        mem_cas_n <= '0';
                        mem_we_n <= '0';
                        mem_addr(COL_WIDTH-1 downto 0) <= col_addr;
                        mem_bank <= bank_addr;
                        state <= WRITE_DATA;
                    end if;

                when WRITE_DATA =>
                    mem_dq_out <= req_data;
                    mem_dq_oe <= '1';
                    state <= IDLE;

                when READ_WAIT =>
                    if timer = 0 then
                        state <= READ_DATA;
                    end if;

                when READ_DATA =>
                    user_data_out <= mem_dq_in;
                    user_valid <= '1';
                    state <= IDLE;

                when PRECHARGE =>
                    mem_ras_n <= '0';
                    mem_we_n <= '0';
                    mem_bank <= bank_addr;
                    bank_idx := to_int(bank_addr);
                    bank_states(bank_idx) <= BANK_IDLE;
                    timer <= tRP;
                    state <= ACTIVE;

                when REFRESH =>
                    mem_ras_n <= '0';
                    mem_cas_n <= '0';
                    refresh_counter <= 0;
                    timer <= tRFC;
                    -- Invalidate all bank states
                    for i in 0 to 3 loop
                        bank_states(i) <= BANK_IDLE;
                    end loop;
                    state <= IDLE;

            end case;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity MemoryController_TB is
end MemoryController_TB;

architecture Test of MemoryController_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_user_addr : bit_vector(23 downto 0);
    signal tb_user_din, tb_user_dout : bit_vector(15 downto 0);
    signal tb_user_read, tb_user_write : bit;
    signal tb_user_ready, tb_user_valid : bit;
    signal tb_mem_dq_in : bit_vector(15 downto 0);
begin
    UUT: entity work.MemoryController
        port map (
            clk => tb_clk, reset => tb_reset,
            user_addr => tb_user_addr,
            user_data_in => tb_user_din,
            user_data_out => tb_user_dout,
            user_read => tb_user_read,
            user_write => tb_user_write,
            user_ready => tb_user_ready,
            user_valid => tb_user_valid,
            mem_dq_in => tb_mem_dq_in
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    tb_mem_dq_in <= "1010101010101010";  -- Dummy read data

    stimulus: process
    begin
        tb_reset <= '1';
        tb_user_addr <= (others => '0');
        tb_user_din <= (others => '0');
        tb_user_read <= '0';
        tb_user_write <= '0';
        wait for 100 ns;

        tb_reset <= '0';
        wait for 3000 ns;  -- Wait for init

        -- Write
        wait until tb_user_ready = '1';
        tb_user_addr <= "000000000000000100000000";
        tb_user_din <= "1111000011110000";
        tb_user_write <= '1';
        wait for 10 ns;
        tb_user_write <= '0';

        wait for 200 ns;

        -- Read
        wait until tb_user_ready = '1';
        tb_user_addr <= "000000000000000100000000";
        tb_user_read <= '1';
        wait for 10 ns;
        tb_user_read <= '0';

        wait for 200 ns;
        wait;
    end process;
end Test;
