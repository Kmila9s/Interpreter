-- ============================================================================
-- VHDL Project: LeadingZeroCount
-- Category: Arithmetic & Mathematics
-- Complexity: Low
-- Description: Count Leading Zeros (CLZ) and Count Leading Ones (CLO)
--              Essential for normalization in floating-point operations
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity LeadingZeroCount is
    generic (
        WIDTH : integer := 32
    );
    port (
        data_in         : in  bit_vector(WIDTH-1 downto 0);
        mode            : in  bit;  -- '0' = count zeros, '1' = count ones
        count           : out bit_vector(5 downto 0);  -- Up to 32
        all_same        : out bit   -- All bits are same (all zeros or all ones)
    );
end LeadingZeroCount;

architecture Behavioral of LeadingZeroCount is
    signal effective_data : bit_vector(WIDTH-1 downto 0);
    signal clz_result : integer range 0 to WIDTH;
begin
    -- Invert data for CLO mode
    invert_gen: for i in 0 to WIDTH-1 generate
        effective_data(i) <= data_in(i) xor mode;
    end generate;

    -- Count leading zeros
    clz_process: process(effective_data)
        variable cnt : integer range 0 to WIDTH;
        variable found : bit;
    begin
        cnt := 0;
        found := '0';

        -- Scan from MSB to LSB
        for i in WIDTH-1 downto 0 loop
            if found = '0' then
                if effective_data(i) = '0' then
                    cnt := cnt + 1;
                else
                    found := '1';
                end if;
            end if;
        end loop;

        clz_result <= cnt;
    end process;

    -- Convert to bit vector
    count_conv: process(clz_result)
        variable temp : integer;
    begin
        temp := clz_result;
        for i in 0 to 5 loop
            if (temp mod 2) = 1 then
                count(i) <= '1';
            else
                count(i) <= '0';
            end if;
            temp := temp / 2;
        end loop;
    end process;

    -- All same detection
    all_same <= '1' when clz_result = WIDTH else '0';

end Behavioral;

-- ============================================================================
-- Parallel Prefix CLZ (faster, logarithmic depth)
-- ============================================================================
entity CLZ32_Fast is
    port (
        data_in : in  bit_vector(31 downto 0);
        count   : out bit_vector(5 downto 0);
        zero    : out bit  -- Input is all zeros
    );
end CLZ32_Fast;

architecture Behavioral of CLZ32_Fast is
    -- Level 1: 16 pairs
    signal or16 : bit_vector(15 downto 0);
    signal sel16 : bit_vector(15 downto 0);

    -- Level 2: 8 groups
    signal or8 : bit_vector(7 downto 0);
    signal sel8 : bit_vector(7 downto 0);

    -- Level 3: 4 groups
    signal or4 : bit_vector(3 downto 0);
    signal sel4 : bit_vector(3 downto 0);

    -- Level 4: 2 groups
    signal or2 : bit_vector(1 downto 0);
    signal sel2 : bit_vector(1 downto 0);

    -- Level 5: 1 group
    signal or1 : bit;
    signal sel1 : bit;

begin
    -- Level 1: OR pairs of bits, select which has the leading 1
    level1: for i in 0 to 15 generate
        or16(i) <= data_in(2*i+1) or data_in(2*i);
        sel16(i) <= data_in(2*i+1);  -- Select upper bit if it's 1
    end generate;

    -- Level 2: OR pairs of level 1
    level2: for i in 0 to 7 generate
        or8(i) <= or16(2*i+1) or or16(2*i);
        sel8(i) <= or16(2*i+1);
    end generate;

    -- Level 3: OR pairs of level 2
    level3: for i in 0 to 3 generate
        or4(i) <= or8(2*i+1) or or8(2*i);
        sel4(i) <= or8(2*i+1);
    end generate;

    -- Level 4: OR pairs of level 3
    level4: for i in 0 to 1 generate
        or2(i) <= or4(2*i+1) or or4(2*i);
        sel2(i) <= or4(2*i+1);
    end generate;

    -- Level 5: Final
    or1 <= or2(1) or or2(0);
    sel1 <= or2(1);

    -- Build count from selections
    count_build: process(sel1, sel2, sel4, sel8, sel16, or1, or2, or4, or8, or16, data_in)
        variable idx : integer range 0 to 31;
        variable c : bit_vector(5 downto 0);
    begin
        -- If all zeros, count = 32
        if or1 = '0' then
            c := "100000";  -- 32
        else
            -- Bit 4: which half of 32
            c(4) := not sel1;

            -- Bit 3: which quarter
            if sel1 = '1' then
                c(3) := not sel2(1);
            else
                c(3) := not sel2(0);
            end if;

            -- Bit 2: which eighth
            idx := 0;
            if c(4) = '1' then idx := idx + 2; end if;
            if c(3) = '1' then idx := idx + 1; end if;
            c(2) := not sel4(3 - idx);

            -- Bit 1: which sixteenth
            idx := 0;
            if c(4) = '1' then idx := idx + 4; end if;
            if c(3) = '1' then idx := idx + 2; end if;
            if c(2) = '1' then idx := idx + 1; end if;
            c(1) := not sel8(7 - idx);

            -- Bit 0: final bit
            idx := 0;
            if c(4) = '1' then idx := idx + 8; end if;
            if c(3) = '1' then idx := idx + 4; end if;
            if c(2) = '1' then idx := idx + 2; end if;
            if c(1) = '1' then idx := idx + 1; end if;
            c(0) := not sel16(15 - idx);

            c(5) := '0';
        end if;

        count <= c;
    end process;

    zero <= not or1;

end Behavioral;

-- ============================================================================
-- Find First Set (FFS) - returns position of first '1' from LSB
-- ============================================================================
entity FindFirstSet is
    generic (
        WIDTH : integer := 32
    );
    port (
        data_in     : in  bit_vector(WIDTH-1 downto 0);
        position    : out bit_vector(5 downto 0);
        found       : out bit
    );
end FindFirstSet;

architecture Behavioral of FindFirstSet is
begin
    ffs_process: process(data_in)
        variable pos : integer range 0 to WIDTH;
        variable fnd : bit;
    begin
        pos := 0;
        fnd := '0';

        -- Scan from LSB to MSB
        for i in 0 to WIDTH-1 loop
            if fnd = '0' and data_in(i) = '1' then
                pos := i;
                fnd := '1';
            end if;
        end loop;

        -- Convert to bit vector
        for i in 0 to 5 loop
            if (pos mod 2) = 1 then
                position(i) <= '1';
            else
                position(i) <= '0';
            end if;
            pos := pos / 2;
        end loop;

        found <= fnd;
    end process;

end Behavioral;

-- ============================================================================
-- Count Trailing Zeros (CTZ)
-- ============================================================================
entity CountTrailingZeros is
    generic (
        WIDTH : integer := 32
    );
    port (
        data_in     : in  bit_vector(WIDTH-1 downto 0);
        count       : out bit_vector(5 downto 0);
        all_zeros   : out bit
    );
end CountTrailingZeros;

architecture Behavioral of CountTrailingZeros is
begin
    ctz_process: process(data_in)
        variable cnt : integer range 0 to WIDTH;
        variable found : bit;
    begin
        cnt := 0;
        found := '0';

        -- Scan from LSB to MSB
        for i in 0 to WIDTH-1 loop
            if found = '0' then
                if data_in(i) = '0' then
                    cnt := cnt + 1;
                else
                    found := '1';
                end if;
            end if;
        end loop;

        -- Convert to bit vector
        for i in 0 to 5 loop
            if (cnt mod 2) = 1 then
                count(i) <= '1';
            else
                count(i) <= '0';
            end if;
            cnt := cnt / 2;
        end loop;

        all_zeros <= not found;
    end process;

end Behavioral;

-- ============================================================================
-- Bit Field Extractor
-- ============================================================================
entity BitFieldExtract is
    generic (
        DATA_WIDTH : integer := 32;
        MAX_FIELD  : integer := 16
    );
    port (
        data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        start_pos   : in  bit_vector(4 downto 0);  -- Start position (0-31)
        field_len   : in  bit_vector(4 downto 0);  -- Length (1-16)
        sign_extend : in  bit;
        field_out   : out bit_vector(MAX_FIELD-1 downto 0)
    );
end BitFieldExtract;

architecture Behavioral of BitFieldExtract is
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
    extract_proc: process(data_in, start_pos, field_len, sign_extend)
        variable start : integer;
        variable len : integer;
        variable sign_bit : bit;
    begin
        start := to_integer(start_pos);
        len := to_integer(field_len);
        if len = 0 then len := 1; end if;
        if len > MAX_FIELD then len := MAX_FIELD; end if;

        -- Extract field
        for i in 0 to MAX_FIELD-1 loop
            if i < len then
                if (start + i) < DATA_WIDTH then
                    field_out(i) <= data_in(start + i);
                else
                    field_out(i) <= '0';
                end if;
            else
                -- Sign or zero extend
                if sign_extend = '1' then
                    if (start + len - 1) < DATA_WIDTH then
                        sign_bit := data_in(start + len - 1);
                    else
                        sign_bit := '0';
                    end if;
                    field_out(i) <= sign_bit;
                else
                    field_out(i) <= '0';
                end if;
            end if;
        end loop;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity LeadingZeroCount_TB is
end LeadingZeroCount_TB;

architecture Test of LeadingZeroCount_TB is
    signal tb_data : bit_vector(31 downto 0);
    signal tb_mode : bit;
    signal tb_count : bit_vector(5 downto 0);
    signal tb_all_same : bit;
begin
    UUT: entity work.LeadingZeroCount
        generic map (WIDTH => 32)
        port map (
            data_in  => tb_data,
            mode     => tb_mode,
            count    => tb_count,
            all_same => tb_all_same
        );

    stimulus: process
    begin
        tb_mode <= '0';  -- Count zeros

        -- All zeros: CLZ = 32
        tb_data <= "00000000000000000000000000000000";
        wait for 10 ns;

        -- Leading 1: CLZ = 0
        tb_data <= "10000000000000000000000000000000";
        wait for 10 ns;

        -- CLZ = 4
        tb_data <= "00001000000000000000000000000000";
        wait for 10 ns;

        -- CLZ = 16
        tb_data <= "00000000000000001000000000000000";
        wait for 10 ns;

        -- Test CLO mode
        tb_mode <= '1';
        tb_data <= "11111111000000000000000000000000";
        wait for 10 ns;

        tb_data <= "11111111111111111111111111111111";
        wait for 10 ns;

        wait;
    end process;
end Test;
