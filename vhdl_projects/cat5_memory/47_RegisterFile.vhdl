-- ============================================================================
-- VHDL Project: RegisterFile
-- Category: Memory Structures
-- Complexity: Medium
-- Description: Multi-port register file for processor designs
--              Configurable read/write ports, bypass logic
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity RegisterFile is
    generic (
        DATA_WIDTH   : integer := 32;
        ADDR_WIDTH   : integer := 5;   -- 32 registers
        NUM_READ     : integer := 2;
        NUM_WRITE    : integer := 1
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Write port (single)
        write_en    : in  bit;
        write_addr  : in  bit_vector(ADDR_WIDTH-1 downto 0);
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Read ports (dual)
        read_addr_1 : in  bit_vector(ADDR_WIDTH-1 downto 0);
        read_data_1 : out bit_vector(DATA_WIDTH-1 downto 0);
        read_addr_2 : in  bit_vector(ADDR_WIDTH-1 downto 0);
        read_data_2 : out bit_vector(DATA_WIDTH-1 downto 0)
    );
end RegisterFile;

architecture Behavioral of RegisterFile is
    constant NUM_REGS : integer := 2 ** ADDR_WIDTH;
    type reg_array is array (0 to NUM_REGS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal registers : reg_array;

    function to_integer(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then
                result := result + power;
            end if;
            power := power * 2;
        end loop;
        return result;
    end function;

    function addr_equal(a, b : bit_vector) return bit is
    begin
        for i in a'range loop
            if a(i) /= b(i) then
                return '0';
            end if;
        end loop;
        return '1';
    end function;

begin
    -- Write process (synchronous)
    write_proc: process(clk, reset)
        variable addr_int : integer;
    begin
        if reset = '1' then
            for i in 0 to NUM_REGS-1 loop
                registers(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            if write_en = '1' then
                addr_int := to_integer(write_addr);
                -- Register 0 is typically hardwired to 0 in RISC
                if addr_int /= 0 then
                    registers(addr_int) <= write_data;
                end if;
            end if;
        end if;
    end process;

    -- Read process (combinational with bypass)
    read_proc: process(read_addr_1, read_addr_2, registers, write_en, write_addr, write_data)
        variable addr1_int, addr2_int : integer;
    begin
        addr1_int := to_integer(read_addr_1);
        addr2_int := to_integer(read_addr_2);

        -- Read port 1 with bypass
        if addr1_int = 0 then
            read_data_1 <= (others => '0');
        elsif write_en = '1' and addr_equal(read_addr_1, write_addr) = '1' then
            read_data_1 <= write_data;  -- Bypass
        else
            read_data_1 <= registers(addr1_int);
        end if;

        -- Read port 2 with bypass
        if addr2_int = 0 then
            read_data_2 <= (others => '0');
        elsif write_en = '1' and addr_equal(read_addr_2, write_addr) = '1' then
            read_data_2 <= write_data;  -- Bypass
        else
            read_data_2 <= registers(addr2_int);
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Three-Port Register File (2R1W)
-- ============================================================================
entity RegisterFile3Port is
    generic (
        DATA_WIDTH : integer := 32;
        ADDR_WIDTH : integer := 5
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Write port
        we          : in  bit;
        wa          : in  bit_vector(ADDR_WIDTH-1 downto 0);
        wd          : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Read port A
        ra1         : in  bit_vector(ADDR_WIDTH-1 downto 0);
        rd1         : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Read port B
        ra2         : in  bit_vector(ADDR_WIDTH-1 downto 0);
        rd2         : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Read port C (extra)
        ra3         : in  bit_vector(ADDR_WIDTH-1 downto 0);
        rd3         : out bit_vector(DATA_WIDTH-1 downto 0)
    );
end RegisterFile3Port;

architecture Behavioral of RegisterFile3Port is
    constant NUM_REGS : integer := 2 ** ADDR_WIDTH;
    type reg_array is array (0 to NUM_REGS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal regs : reg_array;

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then
                result := result + power;
            end if;
            power := power * 2;
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
    begin
        if reset = '1' then
            for i in 0 to NUM_REGS-1 loop
                regs(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            if we = '1' and to_int(wa) /= 0 then
                regs(to_int(wa)) <= wd;
            end if;
        end if;
    end process;

    -- Combinational reads
    rd1 <= (others => '0') when to_int(ra1) = 0 else regs(to_int(ra1));
    rd2 <= (others => '0') when to_int(ra2) = 0 else regs(to_int(ra2));
    rd3 <= (others => '0') when to_int(ra3) = 0 else regs(to_int(ra3));

end Behavioral;

-- ============================================================================
-- Banked Register File (for SIMD/Vector processors)
-- ============================================================================
entity BankedRegisterFile is
    generic (
        DATA_WIDTH : integer := 32;
        ADDR_WIDTH : integer := 4;   -- 16 regs per bank
        NUM_BANKS  : integer := 4
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Bank select
        bank_sel    : in  bit_vector(1 downto 0);
        -- Write port
        write_en    : in  bit;
        write_addr  : in  bit_vector(ADDR_WIDTH-1 downto 0);
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        write_all   : in  bit;  -- Write to all banks
        -- Read port
        read_addr   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        read_data   : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Vector read (all banks)
        vec_read_addr : in  bit_vector(ADDR_WIDTH-1 downto 0);
        vec_read_data : out bit_vector(NUM_BANKS*DATA_WIDTH-1 downto 0)
    );
end BankedRegisterFile;

architecture Behavioral of BankedRegisterFile is
    constant NUM_REGS : integer := 2 ** ADDR_WIDTH;
    type reg_array is array (0 to NUM_REGS-1) of bit_vector(DATA_WIDTH-1 downto 0);
    type bank_array is array (0 to NUM_BANKS-1) of reg_array;

    signal banks : bank_array;

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then
                result := result + power;
            end if;
            power := power * 2;
        end loop;
        return result;
    end function;

begin
    write_proc: process(clk, reset)
        variable bank_idx, addr_idx : integer;
    begin
        if reset = '1' then
            for b in 0 to NUM_BANKS-1 loop
                for r in 0 to NUM_REGS-1 loop
                    banks(b)(r) <= (others => '0');
                end loop;
            end loop;
        elsif clk'event and clk = '1' then
            if write_en = '1' then
                addr_idx := to_int(write_addr);
                if write_all = '1' then
                    -- Write to all banks
                    for b in 0 to NUM_BANKS-1 loop
                        banks(b)(addr_idx) <= write_data;
                    end loop;
                else
                    -- Write to selected bank
                    bank_idx := to_int(bank_sel);
                    banks(bank_idx)(addr_idx) <= write_data;
                end if;
            end if;
        end if;
    end process;

    -- Single read
    read_data <= banks(to_int(bank_sel))(to_int(read_addr));

    -- Vector read (all banks at once)
    vec_read_gen: for i in 0 to NUM_BANKS-1 generate
        vec_read_data((i+1)*DATA_WIDTH-1 downto i*DATA_WIDTH) <=
            banks(i)(to_int(vec_read_addr));
    end generate;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity RegisterFile_TB is
end RegisterFile_TB;

architecture Test of RegisterFile_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_write_en : bit;
    signal tb_write_addr : bit_vector(4 downto 0);
    signal tb_write_data : bit_vector(31 downto 0);
    signal tb_read_addr_1, tb_read_addr_2 : bit_vector(4 downto 0);
    signal tb_read_data_1, tb_read_data_2 : bit_vector(31 downto 0);
begin
    UUT: entity work.RegisterFile
        generic map (DATA_WIDTH => 32, ADDR_WIDTH => 5)
        port map (
            clk         => tb_clk,
            reset       => tb_reset,
            write_en    => tb_write_en,
            write_addr  => tb_write_addr,
            write_data  => tb_write_data,
            read_addr_1 => tb_read_addr_1,
            read_data_1 => tb_read_data_1,
            read_addr_2 => tb_read_addr_2,
            read_data_2 => tb_read_data_2
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_write_en <= '0';
        tb_write_addr <= (others => '0');
        tb_write_data <= (others => '0');
        tb_read_addr_1 <= (others => '0');
        tb_read_addr_2 <= (others => '0');
        wait for 30 ns;

        tb_reset <= '0';

        -- Write to register 1
        tb_write_en <= '1';
        tb_write_addr <= "00001";
        tb_write_data <= "11110000111100001111000011110000";
        wait for 10 ns;

        -- Write to register 2
        tb_write_addr <= "00010";
        tb_write_data <= "10101010101010101010101010101010";
        wait for 10 ns;

        tb_write_en <= '0';

        -- Read registers
        tb_read_addr_1 <= "00001";
        tb_read_addr_2 <= "00010";
        wait for 10 ns;

        -- Test bypass: write and read same register
        tb_write_en <= '1';
        tb_write_addr <= "00011";
        tb_write_data <= "11111111111111111111111111111111";
        tb_read_addr_1 <= "00011";  -- Should see new data via bypass
        wait for 10 ns;

        -- Read register 0 (should be 0)
        tb_write_en <= '0';
        tb_read_addr_1 <= "00000";
        wait for 10 ns;

        wait;
    end process;
end Test;
