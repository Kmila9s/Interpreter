-- ============================================================================
-- VHDL Project: PopulationCount
-- Category: Arithmetic & Mathematics
-- Complexity: Medium
-- Description: Population count (Hamming weight) - count number of 1s
--              Multiple implementations: iterative, tree, lookup table
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity PopulationCount is
    generic (
        WIDTH : integer := 32
    );
    port (
        data_in     : in  bit_vector(WIDTH-1 downto 0);
        count       : out bit_vector(5 downto 0);  -- Up to 32 ones
        odd_parity  : out bit;  -- Odd number of 1s
        even_parity : out bit   -- Even number of 1s
    );
end PopulationCount;

architecture Behavioral of PopulationCount is
    signal popcount : integer range 0 to WIDTH;
begin
    -- Iterative counting
    count_proc: process(data_in)
        variable cnt : integer range 0 to WIDTH;
    begin
        cnt := 0;
        for i in 0 to WIDTH-1 loop
            if data_in(i) = '1' then
                cnt := cnt + 1;
            end if;
        end loop;
        popcount <= cnt;
    end process;

    -- Convert to bit vector
    conv_proc: process(popcount)
        variable temp : integer;
    begin
        temp := popcount;
        for i in 0 to 5 loop
            if (temp mod 2) = 1 then
                count(i) <= '1';
            else
                count(i) <= '0';
            end if;
            temp := temp / 2;
        end loop;
    end process;

    -- Parity
    parity_proc: process(popcount)
    begin
        if (popcount mod 2) = 1 then
            odd_parity <= '1';
            even_parity <= '0';
        else
            odd_parity <= '0';
            even_parity <= '1';
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Tree-based Population Count (8-bit, for building larger)
-- ============================================================================
entity PopCount8 is
    port (
        data_in : in  bit_vector(7 downto 0);
        count   : out bit_vector(3 downto 0)
    );
end PopCount8;

architecture Tree of PopCount8 is
    -- Level 1: sum pairs of bits (4 sums of 2 bits each)
    signal s1 : bit_vector(7 downto 0);  -- 4 x 2-bit sums

    -- Level 2: sum pairs of level 1 (2 sums of 3 bits each)
    signal s2 : bit_vector(5 downto 0);  -- 2 x 3-bit sums

    -- Level 3: final sum (1 sum of 4 bits)
    signal s3 : bit_vector(3 downto 0);

    -- Full adder function
    function fa_sum(a, b, c : bit) return bit is
    begin
        return a xor b xor c;
    end function;

    function fa_carry(a, b, c : bit) return bit is
    begin
        return (a and b) or (a and c) or (b and c);
    end function;

    -- Half adder
    function ha_sum(a, b : bit) return bit is
    begin
        return a xor b;
    end function;

    function ha_carry(a, b : bit) return bit is
    begin
        return a and b;
    end function;

begin
    -- Level 1: Add pairs of bits
    -- data_in(1:0) -> s1(1:0)
    s1(0) <= ha_sum(data_in(0), data_in(1));
    s1(1) <= ha_carry(data_in(0), data_in(1));

    -- data_in(3:2) -> s1(3:2)
    s1(2) <= ha_sum(data_in(2), data_in(3));
    s1(3) <= ha_carry(data_in(2), data_in(3));

    -- data_in(5:4) -> s1(5:4)
    s1(4) <= ha_sum(data_in(4), data_in(5));
    s1(5) <= ha_carry(data_in(4), data_in(5));

    -- data_in(7:6) -> s1(7:6)
    s1(6) <= ha_sum(data_in(6), data_in(7));
    s1(7) <= ha_carry(data_in(6), data_in(7));

    -- Level 2: Add pairs of 2-bit values
    -- s1(1:0) + s1(3:2) -> s2(2:0)
    s2(0) <= ha_sum(s1(0), s1(2));
    s2(1) <= fa_sum(s1(1), s1(3), ha_carry(s1(0), s1(2)));
    s2(2) <= fa_carry(s1(1), s1(3), ha_carry(s1(0), s1(2)));

    -- s1(5:4) + s1(7:6) -> s2(5:3)
    s2(3) <= ha_sum(s1(4), s1(6));
    s2(4) <= fa_sum(s1(5), s1(7), ha_carry(s1(4), s1(6)));
    s2(5) <= fa_carry(s1(5), s1(7), ha_carry(s1(4), s1(6)));

    -- Level 3: Add two 3-bit values
    -- s2(2:0) + s2(5:3) -> s3(3:0)
    s3(0) <= ha_sum(s2(0), s2(3));
    s3(1) <= fa_sum(s2(1), s2(4), ha_carry(s2(0), s2(3)));
    s3(2) <= fa_sum(s2(2), s2(5), fa_carry(s2(1), s2(4), ha_carry(s2(0), s2(3))));
    s3(3) <= fa_carry(s2(2), s2(5), fa_carry(s2(1), s2(4), ha_carry(s2(0), s2(3))));

    count <= s3;

end Tree;

-- ============================================================================
-- 32-bit Population Count using 8-bit blocks
-- ============================================================================
entity PopCount32 is
    port (
        data_in : in  bit_vector(31 downto 0);
        count   : out bit_vector(5 downto 0)
    );
end PopCount32;

architecture Structural of PopCount32 is
    signal cnt0, cnt1, cnt2, cnt3 : bit_vector(3 downto 0);
    signal sum01, sum23 : bit_vector(4 downto 0);
    signal total : bit_vector(5 downto 0);

    function add4(a, b : bit_vector(3 downto 0)) return bit_vector is
        variable result : bit_vector(4 downto 0);
        variable carry : bit := '0';
    begin
        for i in 0 to 3 loop
            result(i) := a(i) xor b(i) xor carry;
            carry := (a(i) and b(i)) or (a(i) and carry) or (b(i) and carry);
        end loop;
        result(4) := carry;
        return result;
    end function;

    function add5(a, b : bit_vector(4 downto 0)) return bit_vector is
        variable result : bit_vector(5 downto 0);
        variable carry : bit := '0';
    begin
        for i in 0 to 4 loop
            result(i) := a(i) xor b(i) xor carry;
            carry := (a(i) and b(i)) or (a(i) and carry) or (b(i) and carry);
        end loop;
        result(5) := carry;
        return result;
    end function;

begin
    -- Count each byte
    pc0: entity work.PopCount8 port map (data_in(7 downto 0), cnt0);
    pc1: entity work.PopCount8 port map (data_in(15 downto 8), cnt1);
    pc2: entity work.PopCount8 port map (data_in(23 downto 16), cnt2);
    pc3: entity work.PopCount8 port map (data_in(31 downto 24), cnt3);

    -- Sum counts
    sum01 <= add4(cnt0, cnt1);
    sum23 <= add4(cnt2, cnt3);
    total <= add5(sum01, sum23);

    count <= total;

end Structural;

-- ============================================================================
-- Parallel Prefix Population Count (faster)
-- ============================================================================
entity PopCountParallel is
    port (
        data_in : in  bit_vector(31 downto 0);
        count   : out bit_vector(5 downto 0)
    );
end PopCountParallel;

architecture Behavioral of PopCountParallel is
    -- Intermediate results
    signal step1 : bit_vector(31 downto 0);  -- After adding pairs
    signal step2 : bit_vector(31 downto 0);  -- After adding nibbles
    signal step3 : bit_vector(31 downto 0);  -- After adding bytes
    signal step4 : bit_vector(31 downto 0);  -- After adding words
begin
    -- Step 1: Add adjacent pairs of bits
    -- Each pair produces a 2-bit sum in place
    step1_gen: for i in 0 to 15 generate
        signal a, b : bit;
        signal sum : bit_vector(1 downto 0);
    begin
        a <= data_in(2*i);
        b <= data_in(2*i + 1);
        sum(0) <= a xor b;
        sum(1) <= a and b;
        step1(2*i) <= sum(0);
        step1(2*i + 1) <= sum(1);
    end generate;

    -- Step 2: Add adjacent 2-bit counts
    step2_gen: for i in 0 to 7 generate
        signal a, b : bit_vector(1 downto 0);
        signal sum : bit_vector(2 downto 0);
        signal carry : bit;
    begin
        a <= step1(4*i + 1 downto 4*i);
        b <= step1(4*i + 3 downto 4*i + 2);
        sum(0) <= a(0) xor b(0);
        carry <= a(0) and b(0);
        sum(1) <= a(1) xor b(1) xor carry;
        sum(2) <= (a(1) and b(1)) or (a(1) and carry) or (b(1) and carry);
        step2(4*i) <= sum(0);
        step2(4*i + 1) <= sum(1);
        step2(4*i + 2) <= sum(2);
        step2(4*i + 3) <= '0';
    end generate;

    -- Step 3: Add adjacent 4-bit counts
    step3_proc: process(step2)
        variable a, b : bit_vector(3 downto 0);
        variable sum : bit_vector(4 downto 0);
        variable carry : bit;
    begin
        for i in 0 to 3 loop
            a := step2(8*i + 3 downto 8*i);
            b := step2(8*i + 7 downto 8*i + 4);
            carry := '0';
            for j in 0 to 3 loop
                sum(j) := a(j) xor b(j) xor carry;
                carry := (a(j) and b(j)) or (a(j) and carry) or (b(j) and carry);
            end loop;
            sum(4) := carry;
            step3(8*i + 4 downto 8*i) <= sum;
            step3(8*i + 7 downto 8*i + 5) <= "000";
        end loop;
    end process;

    -- Step 4: Add adjacent 8-bit counts
    step4_proc: process(step3)
        variable a, b : bit_vector(4 downto 0);
        variable sum : bit_vector(5 downto 0);
        variable carry : bit;
    begin
        for i in 0 to 1 loop
            a := step3(16*i + 4 downto 16*i);
            b := step3(16*i + 12 downto 16*i + 8);
            carry := '0';
            for j in 0 to 4 loop
                sum(j) := a(j) xor b(j) xor carry;
                carry := (a(j) and b(j)) or (a(j) and carry) or (b(j) and carry);
            end loop;
            sum(5) := carry;
            step4(16*i + 5 downto 16*i) <= sum;
            step4(16*i + 15 downto 16*i + 6) <= "0000000000";
        end loop;
    end process;

    -- Final step: Add the two 16-bit groups
    final_proc: process(step4)
        variable a, b : bit_vector(5 downto 0);
        variable sum : bit_vector(5 downto 0);
        variable carry : bit;
    begin
        a := step4(5 downto 0);
        b := step4(21 downto 16);
        carry := '0';
        for j in 0 to 5 loop
            sum(j) := a(j) xor b(j) xor carry;
            carry := (a(j) and b(j)) or (a(j) and carry) or (b(j) and carry);
        end loop;
        count <= sum;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity PopulationCount_TB is
end PopulationCount_TB;

architecture Test of PopulationCount_TB is
    signal tb_data : bit_vector(31 downto 0);
    signal tb_count : bit_vector(5 downto 0);
    signal tb_odd, tb_even : bit;
begin
    UUT: entity work.PopulationCount
        generic map (WIDTH => 32)
        port map (
            data_in     => tb_data,
            count       => tb_count,
            odd_parity  => tb_odd,
            even_parity => tb_even
        );

    stimulus: process
    begin
        -- All zeros: count = 0
        tb_data <= "00000000000000000000000000000000";
        wait for 10 ns;

        -- All ones: count = 32
        tb_data <= "11111111111111111111111111111111";
        wait for 10 ns;

        -- Single bit: count = 1
        tb_data <= "00000000000000000000000000000001";
        wait for 10 ns;

        -- Alternating: count = 16
        tb_data <= "10101010101010101010101010101010";
        wait for 10 ns;

        -- Count = 8
        tb_data <= "11111111000000000000000000000000";
        wait for 10 ns;

        -- Random pattern
        tb_data <= "11010110010110100111001010110101";
        wait for 10 ns;

        wait;
    end process;
end Test;
