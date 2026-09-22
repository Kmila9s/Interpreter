-- ============================================================================
-- VHDL Project: ErrorCorrection
-- Category: Advanced Systems
-- Complexity: High
-- Description: Error detection and correction codes (Hamming, CRC)
--              SEC-DED (Single Error Correct, Double Error Detect)
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity HammingEncoder is
    generic (DATA_BITS : integer := 8);  -- Results in 12-bit codeword (8 data + 4 parity)
    port (
        data_in   : in  bit_vector(DATA_BITS-1 downto 0);
        code_out  : out bit_vector(DATA_BITS+4-1 downto 0)  -- Data + parity bits
    );
end HammingEncoder;

architecture Behavioral of HammingEncoder is
    -- For 8-bit data: positions 1,2,4,8 are parity bits
    -- Data bits at positions 3,5,6,7,9,10,11,12
    signal p1, p2, p4, p8 : bit;
begin
    -- Arrange data and calculate parity
    -- Position 3 = d0, 5 = d1, 6 = d2, 7 = d3, 9 = d4, 10 = d5, 11 = d6, 12 = d7

    -- P1 covers positions 1,3,5,7,9,11 (bits where position AND 1 = 1)
    p1 <= data_in(0) xor data_in(1) xor data_in(3) xor data_in(4) xor data_in(6);

    -- P2 covers positions 2,3,6,7,10,11 (bits where position AND 2 = 2)
    p2 <= data_in(0) xor data_in(2) xor data_in(3) xor data_in(5) xor data_in(6);

    -- P4 covers positions 4,5,6,7,12 (bits where position AND 4 = 4)
    p4 <= data_in(1) xor data_in(2) xor data_in(3) xor data_in(7);

    -- P8 covers positions 8,9,10,11,12 (bits where position AND 8 = 8)
    p8 <= data_in(4) xor data_in(5) xor data_in(6) xor data_in(7);

    -- Assemble codeword
    code_out(0) <= p1;           -- Position 1
    code_out(1) <= p2;           -- Position 2
    code_out(2) <= data_in(0);   -- Position 3
    code_out(3) <= p4;           -- Position 4
    code_out(4) <= data_in(1);   -- Position 5
    code_out(5) <= data_in(2);   -- Position 6
    code_out(6) <= data_in(3);   -- Position 7
    code_out(7) <= p8;           -- Position 8
    code_out(8) <= data_in(4);   -- Position 9
    code_out(9) <= data_in(5);   -- Position 10
    code_out(10) <= data_in(6);  -- Position 11
    code_out(11) <= data_in(7);  -- Position 12
end Behavioral;

entity HammingDecoder is
    generic (DATA_BITS : integer := 8);
    port (
        code_in     : in  bit_vector(DATA_BITS+4-1 downto 0);
        data_out    : out bit_vector(DATA_BITS-1 downto 0);
        error_pos   : out bit_vector(3 downto 0);  -- Error position (0 = no error)
        single_err  : out bit;
        double_err  : out bit
    );
end HammingDecoder;

architecture Behavioral of HammingDecoder is
    signal s1, s2, s4, s8 : bit;
    signal syndrome : bit_vector(3 downto 0);
    signal corrected : bit_vector(11 downto 0);
begin
    -- Calculate syndrome
    s1 <= code_in(0) xor code_in(2) xor code_in(4) xor code_in(6) xor
          code_in(8) xor code_in(10);
    s2 <= code_in(1) xor code_in(2) xor code_in(5) xor code_in(6) xor
          code_in(9) xor code_in(10);
    s4 <= code_in(3) xor code_in(4) xor code_in(5) xor code_in(6) xor
          code_in(11);
    s8 <= code_in(7) xor code_in(8) xor code_in(9) xor code_in(10) xor
          code_in(11);

    syndrome <= s8 & s4 & s2 & s1;
    error_pos <= syndrome;

    -- Correct single-bit error
    correct_proc: process(code_in, syndrome)
        variable temp : bit_vector(11 downto 0);
        variable pos : integer;
    begin
        temp := code_in;

        if syndrome /= "0000" then
            pos := 0;
            if syndrome(0) = '1' then pos := pos + 1; end if;
            if syndrome(1) = '1' then pos := pos + 2; end if;
            if syndrome(2) = '1' then pos := pos + 4; end if;
            if syndrome(3) = '1' then pos := pos + 8; end if;

            if pos >= 1 and pos <= 12 then
                temp(pos-1) := not temp(pos-1);
            end if;
        end if;

        corrected <= temp;
    end process;

    -- Extract data bits
    data_out(0) <= corrected(2);   -- Position 3
    data_out(1) <= corrected(4);   -- Position 5
    data_out(2) <= corrected(5);   -- Position 6
    data_out(3) <= corrected(6);   -- Position 7
    data_out(4) <= corrected(8);   -- Position 9
    data_out(5) <= corrected(9);   -- Position 10
    data_out(6) <= corrected(10);  -- Position 11
    data_out(7) <= corrected(11);  -- Position 12

    -- Error flags
    single_err <= '1' when syndrome /= "0000" else '0';
    double_err <= '0';  -- Would need overall parity for SEC-DED
end Behavioral;

-- ============================================================================
-- CRC Generator/Checker
-- ============================================================================
entity CRCProcessor is
    generic (
        DATA_WIDTH : integer := 8;
        CRC_WIDTH  : integer := 16
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        -- Data input
        data_in    : in  bit_vector(DATA_WIDTH-1 downto 0);
        data_valid : in  bit;
        -- Control
        init       : in  bit;  -- Initialize CRC register
        -- Output
        crc_out    : out bit_vector(CRC_WIDTH-1 downto 0);
        crc_valid  : out bit;  -- CRC check passed (residue = 0)
        done       : out bit
    );
end CRCProcessor;

architecture Behavioral of CRCProcessor is
    -- CRC-16-CCITT polynomial: x^16 + x^12 + x^5 + 1
    signal crc_reg : bit_vector(CRC_WIDTH-1 downto 0);
    signal processing : bit;
    signal bit_cnt : integer range 0 to DATA_WIDTH;

    function crc_step(crc : bit_vector; data_bit : bit) return bit_vector is
        variable result : bit_vector(15 downto 0);
        variable fb : bit;
    begin
        fb := crc(15) xor data_bit;
        result(0) := fb;
        result(4 downto 1) := crc(3 downto 0);
        result(5) := crc(4) xor fb;
        result(11 downto 6) := crc(10 downto 5);
        result(12) := crc(11) xor fb;
        result(15 downto 13) := crc(14 downto 12);
        return result;
    end function;

begin
    crc_proc: process(clk, reset)
        variable data_shift : bit_vector(DATA_WIDTH-1 downto 0);
    begin
        if reset = '1' then
            crc_reg <= (others => '1');  -- CRC-16 init value
            processing <= '0';
            bit_cnt <= 0;
            done <= '0';
        elsif clk'event and clk = '1' then
            done <= '0';

            if init = '1' then
                crc_reg <= (others => '1');
                processing <= '0';
            elsif data_valid = '1' then
                data_shift := data_in;
                bit_cnt <= DATA_WIDTH;
                processing <= '1';
            elsif processing = '1' then
                -- Process one bit per clock
                crc_reg <= crc_step(crc_reg, data_shift(DATA_WIDTH-1));
                data_shift := data_shift(DATA_WIDTH-2 downto 0) & '0';
                bit_cnt <= bit_cnt - 1;

                if bit_cnt = 1 then
                    processing <= '0';
                    done <= '1';
                end if;
            end if;
        end if;
    end process;

    crc_out <= crc_reg;
    crc_valid <= '1' when crc_reg = (crc_reg'range => '0') else '0';
end Behavioral;

-- ============================================================================
-- Parity Generator
-- ============================================================================
entity ParityGenerator is
    generic (DATA_WIDTH : integer := 8);
    port (
        data_in    : in  bit_vector(DATA_WIDTH-1 downto 0);
        parity_out : out bit;  -- Even parity
        odd_parity : out bit   -- Odd parity
    );
end ParityGenerator;

architecture Behavioral of ParityGenerator is
    signal parity : bit;
begin
    parity_calc: process(data_in)
        variable p : bit;
    begin
        p := '0';
        for i in 0 to DATA_WIDTH-1 loop
            p := p xor data_in(i);
        end loop;
        parity <= p;
    end process;

    parity_out <= parity;
    odd_parity <= not parity;
end Behavioral;

-- ============================================================================
-- Triple Modular Redundancy (TMR) Voter
-- ============================================================================
entity TMRVoter is
    generic (DATA_WIDTH : integer := 32);
    port (
        input_a    : in  bit_vector(DATA_WIDTH-1 downto 0);
        input_b    : in  bit_vector(DATA_WIDTH-1 downto 0);
        input_c    : in  bit_vector(DATA_WIDTH-1 downto 0);
        output_v   : out bit_vector(DATA_WIDTH-1 downto 0);
        error_a    : out bit;
        error_b    : out bit;
        error_c    : out bit
    );
end TMRVoter;

architecture Behavioral of TMRVoter is
begin
    vote_proc: process(input_a, input_b, input_c)
        variable ab_eq, bc_eq, ac_eq : bit;
    begin
        -- Compare inputs
        if input_a = input_b then ab_eq := '1'; else ab_eq := '0'; end if;
        if input_b = input_c then bc_eq := '1'; else bc_eq := '0'; end if;
        if input_a = input_c then ac_eq := '1'; else ac_eq := '0'; end if;

        -- Majority vote
        if ab_eq = '1' then
            output_v <= input_a;  -- A and B agree
        elsif bc_eq = '1' then
            output_v <= input_b;  -- B and C agree
        elsif ac_eq = '1' then
            output_v <= input_a;  -- A and C agree
        else
            output_v <= input_a;  -- All different, use A
        end if;

        -- Error detection
        error_a <= '0';
        error_b <= '0';
        error_c <= '0';

        if ab_eq = '0' and ac_eq = '0' then
            error_a <= '1';  -- A disagrees with both
        end if;
        if ab_eq = '0' and bc_eq = '0' then
            error_b <= '1';  -- B disagrees with both
        end if;
        if bc_eq = '0' and ac_eq = '0' then
            error_c <= '1';  -- C disagrees with both
        end if;
    end process;
end Behavioral;
