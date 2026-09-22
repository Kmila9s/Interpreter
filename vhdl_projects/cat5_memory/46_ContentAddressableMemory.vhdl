-- ============================================================================
-- VHDL Project: ContentAddressableMemory
-- Category: Memory Structures
-- Complexity: High
-- Description: CAM for fast lookup operations (pattern matching)
--              Binary CAM and Ternary CAM (TCAM) implementations
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ContentAddressableMemory is
    generic (
        DATA_WIDTH : integer := 32;
        DEPTH      : integer := 64
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Write interface
        write_en    : in  bit;
        write_addr  : in  bit_vector(5 downto 0);  -- log2(DEPTH)
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Search interface
        search_en   : in  bit;
        search_key  : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Results
        match_found : out bit;
        match_addr  : out bit_vector(5 downto 0);
        match_multi : out bit;  -- Multiple matches
        -- Status
        cam_full    : out bit;
        valid_count : out bit_vector(6 downto 0)
    );
end ContentAddressableMemory;

architecture Behavioral of ContentAddressableMemory is
    type cam_data_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    type valid_type is array (0 to DEPTH-1) of bit;

    signal cam_data : cam_data_type;
    signal valid_bits : valid_type;
    signal num_valid : integer range 0 to DEPTH;

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

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer := val;
    begin
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then
                result(i) := '1';
            else
                result(i) := '0';
            end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

    function data_match(a, b : bit_vector) return bit is
    begin
        for i in a'range loop
            if a(i) /= b(i) then
                return '0';
            end if;
        end loop;
        return '1';
    end function;

begin
    cam_proc: process(clk, reset)
        variable first_match : integer;
        variable match_count : integer;
    begin
        if reset = '1' then
            num_valid <= 0;
            match_found <= '0';
            match_addr <= (others => '0');
            match_multi <= '0';
            for i in 0 to DEPTH-1 loop
                valid_bits(i) <= '0';
                cam_data(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            -- Write operation
            if write_en = '1' then
                cam_data(to_integer(write_addr)) <= write_data;
                if valid_bits(to_integer(write_addr)) = '0' then
                    valid_bits(to_integer(write_addr)) <= '1';
                    num_valid <= num_valid + 1;
                end if;
            end if;

            -- Search operation (parallel compare)
            if search_en = '1' then
                first_match := DEPTH;  -- Invalid default
                match_count := 0;

                for i in 0 to DEPTH-1 loop
                    if valid_bits(i) = '1' and data_match(cam_data(i), search_key) = '1' then
                        if first_match = DEPTH then
                            first_match := i;
                        end if;
                        match_count := match_count + 1;
                    end if;
                end loop;

                if match_count > 0 then
                    match_found <= '1';
                    match_addr <= to_bv(first_match, 6);
                    match_multi <= '1' when match_count > 1 else '0';
                else
                    match_found <= '0';
                    match_multi <= '0';
                end if;
            else
                match_found <= '0';
                match_multi <= '0';
            end if;
        end if;
    end process;

    cam_full <= '1' when num_valid = DEPTH else '0';
    valid_count <= to_bv(num_valid, 7);

end Behavioral;

-- ============================================================================
-- Ternary CAM (TCAM) with don't care support
-- ============================================================================
entity TernaryCAM is
    generic (
        DATA_WIDTH : integer := 32;
        DEPTH      : integer := 32
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Write interface
        write_en    : in  bit;
        write_addr  : in  bit_vector(4 downto 0);
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        write_mask  : in  bit_vector(DATA_WIDTH-1 downto 0);  -- '1' = care, '0' = don't care
        -- Search interface
        search_en   : in  bit;
        search_key  : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Results
        match_found : out bit;
        match_addr  : out bit_vector(4 downto 0);
        match_bitmap: out bit_vector(DEPTH-1 downto 0)  -- All matching entries
    );
end TernaryCAM;

architecture Behavioral of TernaryCAM is
    type tcam_data_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    type tcam_mask_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    type valid_type is array (0 to DEPTH-1) of bit;

    signal tcam_data : tcam_data_type;
    signal tcam_mask : tcam_mask_type;
    signal valid_bits : valid_type;

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

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer := val;
    begin
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then
                result(i) := '1';
            else
                result(i) := '0';
            end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

    -- Ternary match: (key XOR data) AND mask = 0
    function ternary_match(key, data, mask : bit_vector) return bit is
        variable diff : bit;
    begin
        for i in key'range loop
            diff := (key(i) xor data(i)) and mask(i);
            if diff = '1' then
                return '0';
            end if;
        end loop;
        return '1';
    end function;

begin
    tcam_proc: process(clk, reset)
        variable matches : bit_vector(DEPTH-1 downto 0);
        variable first_match : integer;
    begin
        if reset = '1' then
            match_found <= '0';
            match_addr <= (others => '0');
            match_bitmap <= (others => '0');
            for i in 0 to DEPTH-1 loop
                valid_bits(i) <= '0';
                tcam_data(i) <= (others => '0');
                tcam_mask(i) <= (others => '1');  -- All care by default
            end loop;
        elsif clk'event and clk = '1' then
            -- Write operation
            if write_en = '1' then
                tcam_data(to_integer(write_addr)) <= write_data;
                tcam_mask(to_integer(write_addr)) <= write_mask;
                valid_bits(to_integer(write_addr)) <= '1';
            end if;

            -- Search operation
            if search_en = '1' then
                matches := (others => '0');
                first_match := DEPTH;

                for i in 0 to DEPTH-1 loop
                    if valid_bits(i) = '1' then
                        if ternary_match(search_key, tcam_data(i), tcam_mask(i)) = '1' then
                            matches(i) := '1';
                            if first_match = DEPTH then
                                first_match := i;
                            end if;
                        end if;
                    end if;
                end loop;

                match_bitmap <= matches;
                if first_match < DEPTH then
                    match_found <= '1';
                    match_addr <= to_bv(first_match, 5);
                else
                    match_found <= '0';
                end if;
            else
                match_found <= '0';
                match_bitmap <= (others => '0');
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity ContentAddressableMemory_TB is
end ContentAddressableMemory_TB;

architecture Test of ContentAddressableMemory_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_write_en : bit;
    signal tb_write_addr : bit_vector(5 downto 0);
    signal tb_write_data : bit_vector(31 downto 0);
    signal tb_search_en : bit;
    signal tb_search_key : bit_vector(31 downto 0);
    signal tb_match_found : bit;
    signal tb_match_addr : bit_vector(5 downto 0);
    signal tb_match_multi : bit;
    signal tb_cam_full : bit;
    signal tb_valid_count : bit_vector(6 downto 0);
begin
    UUT: entity work.ContentAddressableMemory
        generic map (DATA_WIDTH => 32, DEPTH => 64)
        port map (
            clk         => tb_clk,
            reset       => tb_reset,
            write_en    => tb_write_en,
            write_addr  => tb_write_addr,
            write_data  => tb_write_data,
            search_en   => tb_search_en,
            search_key  => tb_search_key,
            match_found => tb_match_found,
            match_addr  => tb_match_addr,
            match_multi => tb_match_multi,
            cam_full    => tb_cam_full,
            valid_count => tb_valid_count
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
        tb_search_en <= '0';
        tb_search_key <= (others => '0');
        wait for 30 ns;

        tb_reset <= '0';

        -- Write some entries
        tb_write_en <= '1';
        tb_write_addr <= "000000";
        tb_write_data <= "11110000111100001111000011110000";
        wait for 10 ns;

        tb_write_addr <= "000001";
        tb_write_data <= "10101010101010101010101010101010";
        wait for 10 ns;

        tb_write_addr <= "000010";
        tb_write_data <= "11111111000000001111111100000000";
        wait for 10 ns;

        tb_write_en <= '0';

        -- Search for existing entry
        tb_search_en <= '1';
        tb_search_key <= "10101010101010101010101010101010";
        wait for 10 ns;

        -- Search for non-existing entry
        tb_search_key <= "00000000000000000000000000000001";
        wait for 10 ns;

        tb_search_en <= '0';
        wait for 50 ns;
        wait;
    end process;
end Test;
