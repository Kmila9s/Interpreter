-- ============================================================================
-- VHDL Project: Comparator8Bit
-- Category: Digital Logic Fundamentals
-- Complexity: Low
-- Description: 8-bit magnitude comparator with cascading capability
--              Outputs: Greater Than, Less Than, Equal
--              Cascade inputs for building larger comparators
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity Comparator8Bit is
    port (
        A           : in  bit_vector(7 downto 0);
        B           : in  bit_vector(7 downto 0);
        -- Cascade inputs (from less significant comparator)
        GT_in       : in  bit;
        LT_in       : in  bit;
        EQ_in       : in  bit;
        -- Comparison outputs
        GT_out      : out bit;  -- A > B
        LT_out      : out bit;  -- A < B
        EQ_out      : out bit   -- A = B
    );
end Comparator8Bit;

architecture Behavioral of Comparator8Bit is
    -- Internal signals for bit-by-bit comparison
    signal bit_gt : bit_vector(7 downto 0);
    signal bit_lt : bit_vector(7 downto 0);
    signal bit_eq : bit_vector(7 downto 0);

    -- Cascaded comparison signals
    signal cascade_gt : bit_vector(7 downto 0);
    signal cascade_lt : bit_vector(7 downto 0);
    signal cascade_eq : bit_vector(7 downto 0);

begin
    -- Bit-by-bit comparison
    compare_bits: for i in 0 to 7 generate
        bit_gt(i) <= A(i) and (not B(i));  -- A(i)=1 and B(i)=0
        bit_lt(i) <= (not A(i)) and B(i);  -- A(i)=0 and B(i)=1
        bit_eq(i) <= not (A(i) xor B(i));  -- A(i) = B(i)
    end generate compare_bits;

    -- Cascaded comparison from MSB to LSB
    -- Start with cascade inputs for bit 0
    cascade_eq(0) <= bit_eq(0) and EQ_in;
    cascade_gt(0) <= bit_gt(0) or (bit_eq(0) and GT_in);
    cascade_lt(0) <= bit_lt(0) or (bit_eq(0) and LT_in);

    -- Build cascade chain
    cascade_chain: for i in 1 to 7 generate
        -- Equal if this bit equal AND all lower bits equal
        cascade_eq(i) <= bit_eq(i) and cascade_eq(i-1);

        -- Greater if this bit greater OR (this bit equal AND lower bits greater)
        cascade_gt(i) <= bit_gt(i) or (bit_eq(i) and cascade_gt(i-1));

        -- Less if this bit less OR (this bit equal AND lower bits less)
        cascade_lt(i) <= bit_lt(i) or (bit_eq(i) and cascade_lt(i-1));
    end generate cascade_chain;

    -- Final comparison uses MSB cascade result
    -- But we need to check from MSB down, not LSB up
    -- Recalculate with proper priority (MSB has highest priority)

    compare_process: process(A, B, GT_in, LT_in, EQ_in)
        variable gt_temp, lt_temp, eq_temp : bit;
    begin
        -- Start assuming equal
        gt_temp := '0';
        lt_temp := '0';
        eq_temp := '1';

        -- Check from MSB to LSB
        for i in 7 downto 0 loop
            if A(i) = '1' and B(i) = '0' then
                gt_temp := '1';
                lt_temp := '0';
                eq_temp := '0';
                exit;  -- MSB difference determines result
            elsif A(i) = '0' and B(i) = '1' then
                gt_temp := '0';
                lt_temp := '1';
                eq_temp := '0';
                exit;  -- MSB difference determines result
            end if;
            -- If equal, continue to next bit
        end loop;

        -- Apply cascade inputs if local comparison shows equality
        if eq_temp = '1' then
            GT_out <= GT_in;
            LT_out <= LT_in;
            EQ_out <= EQ_in;
        else
            GT_out <= gt_temp;
            LT_out <= lt_temp;
            EQ_out <= '0';
        end if;
    end process compare_process;

end Behavioral;

-- ============================================================================
-- 16-bit Comparator using cascaded 8-bit comparators
-- ============================================================================
entity Comparator16Bit is
    port (
        A       : in  bit_vector(15 downto 0);
        B       : in  bit_vector(15 downto 0);
        GT      : out bit;
        LT      : out bit;
        EQ      : out bit
    );
end Comparator16Bit;

architecture Structural of Comparator16Bit is
    signal low_gt, low_lt, low_eq : bit;
begin
    -- Lower 8 bits comparator
    low_comp: entity work.Comparator8Bit
        port map (
            A      => A(7 downto 0),
            B      => B(7 downto 0),
            GT_in  => '0',
            LT_in  => '0',
            EQ_in  => '1',
            GT_out => low_gt,
            LT_out => low_lt,
            EQ_out => low_eq
        );

    -- Upper 8 bits comparator (cascaded from lower)
    high_comp: entity work.Comparator8Bit
        port map (
            A      => A(15 downto 8),
            B      => B(15 downto 8),
            GT_in  => low_gt,
            LT_in  => low_lt,
            EQ_in  => low_eq,
            GT_out => GT,
            LT_out => LT,
            EQ_out => EQ
        );
end Structural;

-- ============================================================================
-- Signed Comparator (2's complement)
-- ============================================================================
entity SignedComparator8Bit is
    port (
        A       : in  bit_vector(7 downto 0);
        B       : in  bit_vector(7 downto 0);
        GT      : out bit;  -- A > B (signed)
        LT      : out bit;  -- A < B (signed)
        EQ      : out bit   -- A = B
    );
end SignedComparator8Bit;

architecture Behavioral of SignedComparator8Bit is
    signal sign_A, sign_B : bit;
    signal mag_gt, mag_lt, mag_eq : bit;
begin
    sign_A <= A(7);
    sign_B <= B(7);

    -- Use unsigned comparator for magnitude (bits 6 downto 0)
    mag_comp: entity work.Comparator8Bit
        port map (
            A      => '0' & A(6 downto 0),
            B      => '0' & B(6 downto 0),
            GT_in  => '0',
            LT_in  => '0',
            EQ_in  => '1',
            GT_out => mag_gt,
            LT_out => mag_lt,
            EQ_out => mag_eq
        );

    -- Signed comparison logic
    compare: process(sign_A, sign_B, mag_gt, mag_lt, mag_eq, A, B)
    begin
        if sign_A = '0' and sign_B = '1' then
            -- A positive, B negative: A > B
            GT <= '1';
            LT <= '0';
            EQ <= '0';
        elsif sign_A = '1' and sign_B = '0' then
            -- A negative, B positive: A < B
            GT <= '0';
            LT <= '1';
            EQ <= '0';
        elsif sign_A = '0' and sign_B = '0' then
            -- Both positive: use magnitude comparison
            GT <= mag_gt;
            LT <= mag_lt;
            EQ <= mag_eq;
        else
            -- Both negative: reverse magnitude comparison
            GT <= mag_lt;  -- Smaller magnitude = larger negative
            LT <= mag_gt;
            EQ <= mag_eq;
        end if;
    end process compare;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity Comparator_TB is
end Comparator_TB;

architecture Test of Comparator_TB is
    signal tb_A, tb_B : bit_vector(7 downto 0);
    signal tb_GT, tb_LT, tb_EQ : bit;
begin
    UUT: entity work.Comparator8Bit
        port map (
            A      => tb_A,
            B      => tb_B,
            GT_in  => '0',
            LT_in  => '0',
            EQ_in  => '1',
            GT_out => tb_GT,
            LT_out => tb_LT,
            EQ_out => tb_EQ
        );

    stimulus: process
    begin
        tb_A <= "00001111"; tb_B <= "00001111"; wait for 10 ns;  -- Equal
        tb_A <= "11110000"; tb_B <= "00001111"; wait for 10 ns;  -- A > B
        tb_A <= "00001111"; tb_B <= "11110000"; wait for 10 ns;  -- A < B
        tb_A <= "10101010"; tb_B <= "10101001"; wait for 10 ns;  -- A > B (by 1)
        tb_A <= "00000000"; tb_B <= "00000001"; wait for 10 ns;  -- A < B (by 1)
        tb_A <= "11111111"; tb_B <= "11111111"; wait for 10 ns;  -- Equal (max)
        tb_A <= "00000000"; tb_B <= "00000000"; wait for 10 ns;  -- Equal (zero)
        wait;
    end process stimulus;
end Test;
