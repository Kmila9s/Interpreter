-- ============================================================================
-- VHDL Project: RegisterFile
-- Category: Processors
-- Complexity: Medium
-- Description: Multi-port register file with bypass logic
--              Supports simultaneous read/write with forwarding
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity RegisterFile is
    generic (
        DATA_WIDTH : integer := 16;
        ADDR_WIDTH : integer := 4;   -- 16 registers
        NUM_REGS   : integer := 16
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        -- Read ports
        rd_addr1   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        rd_data1   : out bit_vector(DATA_WIDTH-1 downto 0);
        rd_addr2   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        rd_data2   : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Write port
        wr_addr    : in  bit_vector(ADDR_WIDTH-1 downto 0);
        wr_data    : in  bit_vector(DATA_WIDTH-1 downto 0);
        wr_enable  : in  bit;
        -- Bypass control
        bypass_en  : in  bit
    );
end RegisterFile;

architecture Behavioral of RegisterFile is
    type reg_array is array (0 to NUM_REGS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal registers : reg_array;

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

begin
    -- Write process
    write_proc: process(clk, reset)
    begin
        if reset = '1' then
            for i in 0 to NUM_REGS-1 loop
                registers(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            if wr_enable = '1' then
                -- Don't write to R0 (hardwired to zero in some architectures)
                if to_int(wr_addr) /= 0 then
                    registers(to_int(wr_addr)) <= wr_data;
                end if;
            end if;
        end if;
    end process;

    -- Read port 1 with bypass
    read1_proc: process(rd_addr1, wr_addr, wr_data, wr_enable, bypass_en, registers)
    begin
        if bypass_en = '1' and wr_enable = '1' and rd_addr1 = wr_addr then
            -- Forward write data to read port
            rd_data1 <= wr_data;
        else
            if to_int(rd_addr1) = 0 then
                rd_data1 <= (others => '0');  -- R0 always returns 0
            else
                rd_data1 <= registers(to_int(rd_addr1));
            end if;
        end if;
    end process;

    -- Read port 2 with bypass
    read2_proc: process(rd_addr2, wr_addr, wr_data, wr_enable, bypass_en, registers)
    begin
        if bypass_en = '1' and wr_enable = '1' and rd_addr2 = wr_addr then
            rd_data2 <= wr_data;
        else
            if to_int(rd_addr2) = 0 then
                rd_data2 <= (others => '0');
            else
                rd_data2 <= registers(to_int(rd_addr2));
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Triple-port Register File (2 read, 1 write, common in RISC)
-- ============================================================================
entity RegisterFile3Port is
    generic (
        DATA_WIDTH : integer := 32;
        ADDR_WIDTH : integer := 5;   -- 32 registers
        NUM_REGS   : integer := 32
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        -- Read port A (typically Rs)
        rd_addr_a  : in  bit_vector(ADDR_WIDTH-1 downto 0);
        rd_data_a  : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Read port B (typically Rt)
        rd_addr_b  : in  bit_vector(ADDR_WIDTH-1 downto 0);
        rd_data_b  : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Write port (typically Rd)
        wr_addr    : in  bit_vector(ADDR_WIDTH-1 downto 0);
        wr_data    : in  bit_vector(DATA_WIDTH-1 downto 0);
        wr_enable  : in  bit
    );
end RegisterFile3Port;

architecture Behavioral of RegisterFile3Port is
    type reg_array is array (0 to NUM_REGS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal registers : reg_array;

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

begin
    -- Synchronous write
    process(clk, reset)
    begin
        if reset = '1' then
            for i in 0 to NUM_REGS-1 loop
                registers(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            if wr_enable = '1' and to_int(wr_addr) /= 0 then
                registers(to_int(wr_addr)) <= wr_data;
            end if;
        end if;
    end process;

    -- Asynchronous reads with write-through
    rd_data_a <= (others => '0') when to_int(rd_addr_a) = 0 else
                 wr_data when wr_enable = '1' and rd_addr_a = wr_addr else
                 registers(to_int(rd_addr_a));

    rd_data_b <= (others => '0') when to_int(rd_addr_b) = 0 else
                 wr_data when wr_enable = '1' and rd_addr_b = wr_addr else
                 registers(to_int(rd_addr_b));
end Behavioral;

-- ============================================================================
-- Windowed Register File (for SPARC-like architectures)
-- ============================================================================
entity WindowedRegisterFile is
    generic (
        DATA_WIDTH  : integer := 32;
        WINDOW_SIZE : integer := 8;   -- Registers per window section
        NUM_WINDOWS : integer := 8    -- Number of register windows
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Current window pointer
        cwp         : in  bit_vector(2 downto 0);  -- log2(NUM_WINDOWS)
        -- Register access
        reg_addr    : in  bit_vector(4 downto 0);  -- 5-bit register address
        rd_data     : out bit_vector(DATA_WIDTH-1 downto 0);
        wr_data     : in  bit_vector(DATA_WIDTH-1 downto 0);
        wr_enable   : in  bit;
        -- Window operations
        save_window : in  bit;  -- Decrement CWP
        restore_win : in  bit;  -- Increment CWP
        overflow    : out bit;  -- Window overflow trap
        underflow   : out bit   -- Window underflow trap
    );
end WindowedRegisterFile;

architecture Behavioral of WindowedRegisterFile is
    -- Total registers: globals + (NUM_WINDOWS * (ins + locals + outs))
    -- Overlapping: out[n] = in[n+1]
    constant TOTAL_REGS : integer := 8 + NUM_WINDOWS * (WINDOW_SIZE + WINDOW_SIZE);
    type reg_array is array (0 to TOTAL_REGS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal registers : reg_array;

    signal window_ptr : integer range 0 to NUM_WINDOWS-1;
    signal window_valid : bit_vector(NUM_WINDOWS-1 downto 0);

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

begin
    process(clk, reset)
        variable reg_num : integer;
        variable phys_addr : integer;
        variable section : integer;
    begin
        if reset = '1' then
            for i in 0 to TOTAL_REGS-1 loop
                registers(i) <= (others => '0');
            end loop;
            window_ptr <= 0;
            window_valid <= (0 => '1', others => '0');
            overflow <= '0';
            underflow <= '0';
        elsif clk'event and clk = '1' then
            overflow <= '0';
            underflow <= '0';

            -- Window management
            if save_window = '1' then
                if window_ptr = 0 then
                    window_ptr <= NUM_WINDOWS - 1;
                else
                    window_ptr <= window_ptr - 1;
                end if;
                -- Check for overflow
                if window_valid(window_ptr) = '1' then
                    overflow <= '1';
                end if;
            end if;

            if restore_win = '1' then
                if window_ptr = NUM_WINDOWS - 1 then
                    window_ptr <= 0;
                else
                    window_ptr <= window_ptr + 1;
                end if;
                -- Check for underflow
                if window_valid(window_ptr) = '0' then
                    underflow <= '1';
                end if;
            end if;

            -- Register write
            if wr_enable = '1' then
                reg_num := to_int(reg_addr);

                if reg_num < 8 then
                    -- Global registers (r0-r7)
                    if reg_num /= 0 then  -- g0 is always 0
                        registers(reg_num) <= wr_data;
                    end if;
                elsif reg_num < 16 then
                    -- Out registers (r8-r15)
                    phys_addr := 8 + window_ptr * 16 + (reg_num - 8);
                    registers(phys_addr mod TOTAL_REGS) <= wr_data;
                elsif reg_num < 24 then
                    -- Local registers (r16-r23)
                    phys_addr := 8 + window_ptr * 16 + 8 + (reg_num - 16);
                    registers(phys_addr mod TOTAL_REGS) <= wr_data;
                else
                    -- In registers (r24-r31) = previous window's out
                    phys_addr := 8 + ((window_ptr + 1) mod NUM_WINDOWS) * 16 + (reg_num - 24);
                    registers(phys_addr mod TOTAL_REGS) <= wr_data;
                end if;
            end if;
        end if;
    end process;

    -- Read logic
    process(reg_addr, window_ptr, registers)
        variable reg_num : integer;
        variable phys_addr : integer;
    begin
        reg_num := to_int(reg_addr);

        if reg_num = 0 then
            rd_data <= (others => '0');  -- g0 = 0
        elsif reg_num < 8 then
            rd_data <= registers(reg_num);
        elsif reg_num < 16 then
            phys_addr := 8 + window_ptr * 16 + (reg_num - 8);
            rd_data <= registers(phys_addr mod TOTAL_REGS);
        elsif reg_num < 24 then
            phys_addr := 8 + window_ptr * 16 + 8 + (reg_num - 16);
            rd_data <= registers(phys_addr mod TOTAL_REGS);
        else
            phys_addr := 8 + ((window_ptr + 1) mod NUM_WINDOWS) * 16 + (reg_num - 24);
            rd_data <= registers(phys_addr mod TOTAL_REGS);
        end if;
    end process;
end Behavioral;
