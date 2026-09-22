-- ============================================================================
-- VHDL Project: SinglePortRAM
-- Category: Memory Structures
-- Complexity: Low-Medium
-- Description: Configurable single-port RAM with various options
--              Write-first, read-first, and no-change modes
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity SinglePortRAM is
    generic (
        DATA_WIDTH : integer := 8;
        ADDR_WIDTH : integer := 10;  -- 1024 locations
        INIT_VALUE : bit := '0'
    );
    port (
        clk         : in  bit;
        enable      : in  bit;
        write_en    : in  bit;
        address     : in  bit_vector(ADDR_WIDTH-1 downto 0);
        data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        data_out    : out bit_vector(DATA_WIDTH-1 downto 0)
    );
end SinglePortRAM;

architecture WriteFirst of SinglePortRAM is
    constant DEPTH : integer := 2 ** ADDR_WIDTH;
    type ram_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);

    -- Initialize RAM
    function init_ram return ram_type is
        variable result : ram_type;
    begin
        for i in 0 to DEPTH-1 loop
            for j in 0 to DATA_WIDTH-1 loop
                result(i)(j) := INIT_VALUE;
            end loop;
        end loop;
        return result;
    end function;

    signal ram : ram_type := init_ram;

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
    -- Write-first mode: New data appears on output immediately
    process(clk)
        variable addr_int : integer;
    begin
        if clk'event and clk = '1' then
            if enable = '1' then
                addr_int := to_integer(address);
                if write_en = '1' then
                    ram(addr_int) <= data_in;
                    data_out <= data_in;  -- Write-first
                else
                    data_out <= ram(addr_int);
                end if;
            end if;
        end if;
    end process;

end WriteFirst;

architecture ReadFirst of SinglePortRAM is
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
    -- Read-first mode: Old data appears on output during write
    process(clk)
        variable addr_int : integer;
    begin
        if clk'event and clk = '1' then
            if enable = '1' then
                addr_int := to_integer(address);
                data_out <= ram(addr_int);  -- Read old data first
                if write_en = '1' then
                    ram(addr_int) <= data_in;
                end if;
            end if;
        end if;
    end process;

end ReadFirst;

architecture NoChange of SinglePortRAM is
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
    -- No-change mode: Output unchanged during write
    process(clk)
        variable addr_int : integer;
    begin
        if clk'event and clk = '1' then
            if enable = '1' then
                addr_int := to_integer(address);
                if write_en = '1' then
                    ram(addr_int) <= data_in;
                    -- data_out unchanged
                else
                    data_out <= ram(addr_int);
                end if;
            end if;
        end if;
    end process;

end NoChange;

-- ============================================================================
-- RAM with Byte Enable
-- ============================================================================
entity RAMByteEnable is
    generic (
        DATA_WIDTH : integer := 32;
        ADDR_WIDTH : integer := 8
    );
    port (
        clk         : in  bit;
        enable      : in  bit;
        write_en    : in  bit_vector(DATA_WIDTH/8-1 downto 0);  -- Per-byte write enable
        address     : in  bit_vector(ADDR_WIDTH-1 downto 0);
        data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        data_out    : out bit_vector(DATA_WIDTH-1 downto 0)
    );
end RAMByteEnable;

architecture Behavioral of RAMByteEnable is
    constant DEPTH : integer := 2 ** ADDR_WIDTH;
    constant NUM_BYTES : integer := DATA_WIDTH / 8;

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
        variable addr_int : integer;
        variable new_data : bit_vector(DATA_WIDTH-1 downto 0);
    begin
        if clk'event and clk = '1' then
            if enable = '1' then
                addr_int := to_integer(address);

                -- Read
                data_out <= ram(addr_int);

                -- Write with byte enables
                new_data := ram(addr_int);
                for i in 0 to NUM_BYTES-1 loop
                    if write_en(i) = '1' then
                        new_data(i*8+7 downto i*8) := data_in(i*8+7 downto i*8);
                    end if;
                end loop;
                ram(addr_int) <= new_data;
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Initialized ROM
-- ============================================================================
entity InitializedROM is
    generic (
        DATA_WIDTH : integer := 8;
        ADDR_WIDTH : integer := 4   -- 16 locations
    );
    port (
        clk         : in  bit;
        enable      : in  bit;
        address     : in  bit_vector(ADDR_WIDTH-1 downto 0);
        data_out    : out bit_vector(DATA_WIDTH-1 downto 0)
    );
end InitializedROM;

architecture Behavioral of InitializedROM is
    constant DEPTH : integer := 2 ** ADDR_WIDTH;
    type rom_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);

    -- Sine lookup table (scaled 0-255)
    constant ROM : rom_type := (
        "01111111",  -- 0: 127 (sin 0 = 0, offset)
        "10110100",  -- 1: 180
        "11011010",  -- 2: 218
        "11110110",  -- 3: 246
        "11111111",  -- 4: 255 (sin 90 = 1)
        "11110110",  -- 5: 246
        "11011010",  -- 6: 218
        "10110100",  -- 7: 180
        "01111111",  -- 8: 127 (sin 180 = 0)
        "01001011",  -- 9: 75
        "00100101",  -- 10: 37
        "00001001",  -- 11: 9
        "00000000",  -- 12: 0 (sin 270 = -1)
        "00001001",  -- 13: 9
        "00100101",  -- 14: 37
        "01001011"   -- 15: 75
    );

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
            if enable = '1' then
                data_out <= ROM(to_integer(address));
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity SinglePortRAM_TB is
end SinglePortRAM_TB;

architecture Test of SinglePortRAM_TB is
    signal tb_clk, tb_enable, tb_write : bit;
    signal tb_address : bit_vector(9 downto 0);
    signal tb_data_in, tb_data_out : bit_vector(7 downto 0);
begin
    UUT: entity work.SinglePortRAM(WriteFirst)
        generic map (DATA_WIDTH => 8, ADDR_WIDTH => 10)
        port map (
            clk      => tb_clk,
            enable   => tb_enable,
            write_en => tb_write,
            address  => tb_address,
            data_in  => tb_data_in,
            data_out => tb_data_out
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_enable <= '0';
        tb_write <= '0';
        tb_address <= (others => '0');
        tb_data_in <= (others => '0');
        wait for 30 ns;

        tb_enable <= '1';

        -- Write to address 0
        tb_write <= '1';
        tb_address <= "0000000000";
        tb_data_in <= "10101010";
        wait for 10 ns;

        -- Write to address 1
        tb_address <= "0000000001";
        tb_data_in <= "01010101";
        wait for 10 ns;

        -- Read address 0
        tb_write <= '0';
        tb_address <= "0000000000";
        wait for 10 ns;

        -- Read address 1
        tb_address <= "0000000001";
        wait for 10 ns;

        wait;
    end process;
end Test;
