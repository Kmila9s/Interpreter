-- ============================================================================
-- VHDL Project: DualPortRAM
-- Category: Memory Structures
-- Complexity: Medium
-- Description: True dual-port RAM with independent read/write ports
--              Supports simultaneous access, collision handling
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity DualPortRAM is
    generic (
        DATA_WIDTH : integer := 8;
        ADDR_WIDTH : integer := 10
    );
    port (
        -- Port A
        clk_a       : in  bit;
        enable_a    : in  bit;
        write_en_a  : in  bit;
        address_a   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        data_in_a   : in  bit_vector(DATA_WIDTH-1 downto 0);
        data_out_a  : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Port B
        clk_b       : in  bit;
        enable_b    : in  bit;
        write_en_b  : in  bit;
        address_b   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        data_in_b   : in  bit_vector(DATA_WIDTH-1 downto 0);
        data_out_b  : out bit_vector(DATA_WIDTH-1 downto 0)
    );
end DualPortRAM;

architecture Behavioral of DualPortRAM is
    constant DEPTH : integer := 2 ** ADDR_WIDTH;
    type ram_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);

    -- Shared memory
    signal ram : ram_type;

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

begin
    -- Port A process
    port_a_proc: process(clk_a)
        variable addr_int : integer;
    begin
        if clk_a'event and clk_a = '1' then
            if enable_a = '1' then
                addr_int := to_integer(address_a);
                if write_en_a = '1' then
                    ram(addr_int) <= data_in_a;
                end if;
                data_out_a <= ram(addr_int);
            end if;
        end if;
    end process;

    -- Port B process
    port_b_proc: process(clk_b)
        variable addr_int : integer;
    begin
        if clk_b'event and clk_b = '1' then
            if enable_b = '1' then
                addr_int := to_integer(address_b);
                if write_en_b = '1' then
                    ram(addr_int) <= data_in_b;
                end if;
                data_out_b <= ram(addr_int);
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Simple Dual-Port RAM (One write, one read port)
-- ============================================================================
entity SimpleDualPortRAM is
    generic (
        DATA_WIDTH : integer := 8;
        ADDR_WIDTH : integer := 10
    );
    port (
        clk         : in  bit;
        -- Write port
        write_en    : in  bit;
        write_addr  : in  bit_vector(ADDR_WIDTH-1 downto 0);
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Read port
        read_en     : in  bit;
        read_addr   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        read_data   : out bit_vector(DATA_WIDTH-1 downto 0)
    );
end SimpleDualPortRAM;

architecture Behavioral of SimpleDualPortRAM is
    constant DEPTH : integer := 2 ** ADDR_WIDTH;
    type ram_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal ram : ram_type;

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

begin
    process(clk)
    begin
        if clk'event and clk = '1' then
            -- Write
            if write_en = '1' then
                ram(to_integer(write_addr)) <= write_data;
            end if;

            -- Read
            if read_en = '1' then
                read_data <= ram(to_integer(read_addr));
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Dual-Port RAM with Different Widths
-- ============================================================================
entity AsymmetricDPRAM is
    generic (
        WRITE_WIDTH : integer := 32;
        READ_WIDTH  : integer := 8;
        WRITE_DEPTH : integer := 256
    );
    port (
        clk         : in  bit;
        -- Write port (wider)
        write_en    : in  bit;
        write_addr  : in  bit_vector(7 downto 0);  -- 256 x 32
        write_data  : in  bit_vector(WRITE_WIDTH-1 downto 0);
        -- Read port (narrower)
        read_en     : in  bit;
        read_addr   : in  bit_vector(9 downto 0);  -- 1024 x 8
        read_data   : out bit_vector(READ_WIDTH-1 downto 0)
    );
end AsymmetricDPRAM;

architecture Behavioral of AsymmetricDPRAM is
    constant RATIO : integer := WRITE_WIDTH / READ_WIDTH;  -- 4
    constant READ_DEPTH : integer := WRITE_DEPTH * RATIO;

    type ram_type is array (0 to READ_DEPTH-1) of bit_vector(READ_WIDTH-1 downto 0);
    signal ram : ram_type;

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

begin
    process(clk)
        variable wr_addr_base : integer;
        variable rd_addr_int : integer;
    begin
        if clk'event and clk = '1' then
            -- Write wide data as multiple narrow words
            if write_en = '1' then
                wr_addr_base := to_integer(write_addr) * RATIO;
                for i in 0 to RATIO-1 loop
                    ram(wr_addr_base + i) <= write_data((i+1)*READ_WIDTH-1 downto i*READ_WIDTH);
                end loop;
            end if;

            -- Read narrow word
            if read_en = '1' then
                rd_addr_int := to_integer(read_addr);
                read_data <= ram(rd_addr_int);
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Dual-Port RAM with Collision Detection
-- ============================================================================
entity DPRAMCollision is
    generic (
        DATA_WIDTH : integer := 8;
        ADDR_WIDTH : integer := 8
    );
    port (
        clk         : in  bit;
        -- Port A
        en_a        : in  bit;
        we_a        : in  bit;
        addr_a      : in  bit_vector(ADDR_WIDTH-1 downto 0);
        din_a       : in  bit_vector(DATA_WIDTH-1 downto 0);
        dout_a      : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Port B
        en_b        : in  bit;
        we_b        : in  bit;
        addr_b      : in  bit_vector(ADDR_WIDTH-1 downto 0);
        din_b       : in  bit_vector(DATA_WIDTH-1 downto 0);
        dout_b      : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Collision flags
        collision   : out bit;
        write_collision : out bit
    );
end DPRAMCollision;

architecture Behavioral of DPRAMCollision is
    constant DEPTH : integer := 2 ** ADDR_WIDTH;
    type ram_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal ram : ram_type;

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
    process(clk)
        variable addr_a_int, addr_b_int : integer;
        variable same_addr : bit;
    begin
        if clk'event and clk = '1' then
            addr_a_int := to_integer(addr_a);
            addr_b_int := to_integer(addr_b);
            same_addr := addr_equal(addr_a, addr_b);

            -- Collision detection
            collision <= '0';
            write_collision <= '0';

            if en_a = '1' and en_b = '1' and same_addr = '1' then
                collision <= '1';
                if we_a = '1' and we_b = '1' then
                    write_collision <= '1';
                end if;
            end if;

            -- Port A: Priority on write collision
            if en_a = '1' then
                if we_a = '1' then
                    ram(addr_a_int) <= din_a;
                end if;
                dout_a <= ram(addr_a_int);
            end if;

            -- Port B
            if en_b = '1' then
                if we_b = '1' and (same_addr = '0' or we_a = '0') then
                    -- Only write if no conflict with A
                    ram(addr_b_int) <= din_b;
                end if;
                dout_b <= ram(addr_b_int);
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity DualPortRAM_TB is
end DualPortRAM_TB;

architecture Test of DualPortRAM_TB is
    signal tb_clk : bit;
    signal tb_en_a, tb_we_a : bit;
    signal tb_addr_a : bit_vector(9 downto 0);
    signal tb_din_a, tb_dout_a : bit_vector(7 downto 0);
    signal tb_en_b, tb_we_b : bit;
    signal tb_addr_b : bit_vector(9 downto 0);
    signal tb_din_b, tb_dout_b : bit_vector(7 downto 0);
begin
    UUT: entity work.DualPortRAM
        generic map (DATA_WIDTH => 8, ADDR_WIDTH => 10)
        port map (
            clk_a      => tb_clk,
            enable_a   => tb_en_a,
            write_en_a => tb_we_a,
            address_a  => tb_addr_a,
            data_in_a  => tb_din_a,
            data_out_a => tb_dout_a,
            clk_b      => tb_clk,
            enable_b   => tb_en_b,
            write_en_b => tb_we_b,
            address_b  => tb_addr_b,
            data_in_b  => tb_din_b,
            data_out_b => tb_dout_b
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_en_a <= '0'; tb_we_a <= '0';
        tb_addr_a <= (others => '0');
        tb_din_a <= (others => '0');
        tb_en_b <= '0'; tb_we_b <= '0';
        tb_addr_b <= (others => '0');
        tb_din_b <= (others => '0');
        wait for 30 ns;

        -- Port A write
        tb_en_a <= '1'; tb_we_a <= '1';
        tb_addr_a <= "0000000000";
        tb_din_a <= "11110000";
        wait for 10 ns;

        -- Port B write to different address
        tb_we_a <= '0';
        tb_en_b <= '1'; tb_we_b <= '1';
        tb_addr_b <= "0000000001";
        tb_din_b <= "00001111";
        wait for 10 ns;

        -- Simultaneous read
        tb_we_b <= '0';
        tb_addr_a <= "0000000001";
        tb_addr_b <= "0000000000";
        wait for 10 ns;

        wait;
    end process;
end Test;
