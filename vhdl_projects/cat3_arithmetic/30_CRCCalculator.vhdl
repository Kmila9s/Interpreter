-- ============================================================================
-- VHDL Project: CRCCalculator
-- Category: Arithmetic & Mathematics
-- Complexity: High
-- Description: Parallel CRC-8/16/32 generator and checker
--              Supports multiple polynomials, bit/byte-wide input
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity CRCCalculator is
    generic (
        CRC_WIDTH   : integer := 32;
        DATA_WIDTH  : integer := 8
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Control
        init        : in  bit;  -- Initialize CRC register
        enable      : in  bit;  -- Process input data
        -- Configuration
        polynomial  : in  bit_vector(CRC_WIDTH-1 downto 0);
        init_value  : in  bit_vector(CRC_WIDTH-1 downto 0);
        xor_output  : in  bit_vector(CRC_WIDTH-1 downto 0);
        reflect_in  : in  bit;  -- Reflect input bits
        reflect_out : in  bit;  -- Reflect output bits
        -- Data
        data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        crc_out     : out bit_vector(CRC_WIDTH-1 downto 0);
        -- Check mode
        check_crc   : in  bit_vector(CRC_WIDTH-1 downto 0);
        crc_match   : out bit
    );
end CRCCalculator;

architecture Behavioral of CRCCalculator is
    signal crc_reg : bit_vector(CRC_WIDTH-1 downto 0);
    signal crc_final : bit_vector(CRC_WIDTH-1 downto 0);

    function reflect(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
    begin
        for i in val'low to val'high loop
            result(val'high - i + val'low) := val(i);
        end loop;
        return result;
    end function;

    function crc_step(crc : bit_vector; data_bit : bit; poly : bit_vector)
        return bit_vector is
        variable result : bit_vector(crc'range);
        variable feedback : bit;
    begin
        feedback := crc(crc'high) xor data_bit;
        result := crc(crc'high-1 downto crc'low) & '0';
        if feedback = '1' then
            result := result xor poly;
        end if;
        return result;
    end function;

begin
    crc_proc: process(clk, reset)
        variable temp_crc : bit_vector(CRC_WIDTH-1 downto 0);
        variable input_data : bit_vector(DATA_WIDTH-1 downto 0);
    begin
        if reset = '1' then
            crc_reg <= (others => '1');
        elsif clk'event and clk = '1' then
            if init = '1' then
                crc_reg <= init_value;
            elsif enable = '1' then
                -- Reflect input if configured
                if reflect_in = '1' then
                    input_data := reflect(data_in);
                else
                    input_data := data_in;
                end if;

                -- Process each bit of input
                temp_crc := crc_reg;
                for i in DATA_WIDTH-1 downto 0 loop
                    temp_crc := crc_step(temp_crc, input_data(i), polynomial);
                end loop;

                crc_reg <= temp_crc;
            end if;
        end if;
    end process;

    -- Final output processing
    output_proc: process(crc_reg, reflect_out, xor_output)
        variable temp : bit_vector(CRC_WIDTH-1 downto 0);
    begin
        if reflect_out = '1' then
            temp := reflect(crc_reg);
        else
            temp := crc_reg;
        end if;

        crc_final <= temp xor xor_output;
    end process;

    crc_out <= crc_final;

    -- CRC match check
    crc_match <= '1' when crc_final = check_crc else '0';

end Behavioral;

-- ============================================================================
-- CRC-8 Calculator (specific polynomial)
-- ============================================================================
entity CRC8 is
    port (
        clk         : in  bit;
        reset       : in  bit;
        init        : in  bit;
        enable      : in  bit;
        data_in     : in  bit_vector(7 downto 0);
        crc_out     : out bit_vector(7 downto 0)
    );
end CRC8;

architecture Behavioral of CRC8 is
    -- CRC-8-CCITT polynomial: x^8 + x^2 + x + 1 = 0x07
    constant POLY : bit_vector(7 downto 0) := "00000111";
    signal crc_reg : bit_vector(7 downto 0);
begin
    crc_proc: process(clk, reset)
        variable crc : bit_vector(7 downto 0);
        variable feedback : bit;
    begin
        if reset = '1' then
            crc_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            if init = '1' then
                crc_reg <= (others => '0');
            elsif enable = '1' then
                crc := crc_reg;
                -- Process 8 bits in parallel using XOR matrix
                for i in 7 downto 0 loop
                    feedback := crc(7) xor data_in(i);
                    crc := crc(6 downto 0) & '0';
                    if feedback = '1' then
                        crc := crc xor POLY;
                    end if;
                end loop;
                crc_reg <= crc;
            end if;
        end if;
    end process;

    crc_out <= crc_reg;
end Behavioral;

-- ============================================================================
-- CRC-16 Calculator (CRC-16-CCITT)
-- ============================================================================
entity CRC16 is
    port (
        clk         : in  bit;
        reset       : in  bit;
        init        : in  bit;
        enable      : in  bit;
        data_in     : in  bit_vector(7 downto 0);
        crc_out     : out bit_vector(15 downto 0)
    );
end CRC16;

architecture Behavioral of CRC16 is
    -- CRC-16-CCITT polynomial: x^16 + x^12 + x^5 + 1 = 0x1021
    constant POLY : bit_vector(15 downto 0) := "0001000000100001";
    signal crc_reg : bit_vector(15 downto 0);
begin
    crc_proc: process(clk, reset)
        variable crc : bit_vector(15 downto 0);
        variable feedback : bit;
    begin
        if reset = '1' then
            crc_reg <= (others => '1');  -- CCITT init
        elsif clk'event and clk = '1' then
            if init = '1' then
                crc_reg <= (others => '1');
            elsif enable = '1' then
                crc := crc_reg;
                for i in 7 downto 0 loop
                    feedback := crc(15) xor data_in(i);
                    crc := crc(14 downto 0) & '0';
                    if feedback = '1' then
                        crc := crc xor POLY;
                    end if;
                end loop;
                crc_reg <= crc;
            end if;
        end if;
    end process;

    crc_out <= crc_reg;
end Behavioral;

-- ============================================================================
-- CRC-32 Calculator (Ethernet/ZIP)
-- ============================================================================
entity CRC32 is
    port (
        clk         : in  bit;
        reset       : in  bit;
        init        : in  bit;
        enable      : in  bit;
        data_in     : in  bit_vector(7 downto 0);
        crc_out     : out bit_vector(31 downto 0)
    );
end CRC32;

architecture Behavioral of CRC32 is
    -- CRC-32 polynomial: 0x04C11DB7 (Ethernet)
    constant POLY : bit_vector(31 downto 0) := "00000100110000010001110110110111";
    signal crc_reg : bit_vector(31 downto 0);

    function reflect8(val : bit_vector(7 downto 0)) return bit_vector is
        variable result : bit_vector(7 downto 0);
    begin
        for i in 0 to 7 loop
            result(7 - i) := val(i);
        end loop;
        return result;
    end function;

    function reflect32(val : bit_vector(31 downto 0)) return bit_vector is
        variable result : bit_vector(31 downto 0);
    begin
        for i in 0 to 31 loop
            result(31 - i) := val(i);
        end loop;
        return result;
    end function;

begin
    crc_proc: process(clk, reset)
        variable crc : bit_vector(31 downto 0);
        variable feedback : bit;
        variable reflected_data : bit_vector(7 downto 0);
    begin
        if reset = '1' then
            crc_reg <= (others => '1');
        elsif clk'event and clk = '1' then
            if init = '1' then
                crc_reg <= (others => '1');
            elsif enable = '1' then
                crc := crc_reg;
                -- Reflect input byte
                reflected_data := reflect8(data_in);

                for i in 7 downto 0 loop
                    feedback := crc(31) xor reflected_data(i);
                    crc := crc(30 downto 0) & '0';
                    if feedback = '1' then
                        crc := crc xor POLY;
                    end if;
                end loop;
                crc_reg <= crc;
            end if;
        end if;
    end process;

    -- Final output: reflect and complement
    crc_out <= not reflect32(crc_reg);
end Behavioral;

-- ============================================================================
-- Parallel CRC-32 (processes 8 bits in one cycle using combinational logic)
-- ============================================================================
entity CRC32Parallel is
    port (
        clk         : in  bit;
        reset       : in  bit;
        init        : in  bit;
        enable      : in  bit;
        data_in     : in  bit_vector(7 downto 0);
        crc_out     : out bit_vector(31 downto 0)
    );
end CRC32Parallel;

architecture Behavioral of CRC32Parallel is
    signal crc_reg : bit_vector(31 downto 0);
    signal next_crc : bit_vector(31 downto 0);
begin
    -- Parallel CRC calculation (pre-computed XOR matrix)
    -- For CRC32 with polynomial 0x04C11DB7
    parallel_crc: process(crc_reg, data_in)
        variable d : bit_vector(7 downto 0);
        variable c : bit_vector(31 downto 0);
    begin
        d := data_in;
        c := crc_reg;

        -- This is the expanded parallel CRC-32 equation
        -- Each output bit is XOR of input data bits and current CRC bits
        next_crc(0) <= c(24) xor c(30) xor d(0) xor d(6);
        next_crc(1) <= c(24) xor c(25) xor c(30) xor c(31) xor d(0) xor d(1) xor d(6) xor d(7);
        next_crc(2) <= c(24) xor c(25) xor c(26) xor c(30) xor c(31) xor d(0) xor d(1) xor d(2) xor d(6) xor d(7);
        next_crc(3) <= c(25) xor c(26) xor c(27) xor c(31) xor d(1) xor d(2) xor d(3) xor d(7);
        next_crc(4) <= c(24) xor c(26) xor c(27) xor c(28) xor c(30) xor d(0) xor d(2) xor d(3) xor d(4) xor d(6);
        next_crc(5) <= c(24) xor c(25) xor c(27) xor c(28) xor c(29) xor c(30) xor c(31) xor d(0) xor d(1) xor d(3) xor d(4) xor d(5) xor d(6) xor d(7);
        next_crc(6) <= c(25) xor c(26) xor c(28) xor c(29) xor c(30) xor c(31) xor d(1) xor d(2) xor d(4) xor d(5) xor d(6) xor d(7);
        next_crc(7) <= c(24) xor c(26) xor c(27) xor c(29) xor c(31) xor d(0) xor d(2) xor d(3) xor d(5) xor d(7);
        next_crc(8) <= c(0) xor c(24) xor c(25) xor c(27) xor c(28) xor d(0) xor d(1) xor d(3) xor d(4);
        next_crc(9) <= c(1) xor c(25) xor c(26) xor c(28) xor c(29) xor d(1) xor d(2) xor d(4) xor d(5);
        next_crc(10) <= c(2) xor c(24) xor c(26) xor c(27) xor c(29) xor d(0) xor d(2) xor d(3) xor d(5);
        next_crc(11) <= c(3) xor c(24) xor c(25) xor c(27) xor c(28) xor d(0) xor d(1) xor d(3) xor d(4);
        next_crc(12) <= c(4) xor c(24) xor c(25) xor c(26) xor c(28) xor c(29) xor c(30) xor d(0) xor d(1) xor d(2) xor d(4) xor d(5) xor d(6);
        next_crc(13) <= c(5) xor c(25) xor c(26) xor c(27) xor c(29) xor c(30) xor c(31) xor d(1) xor d(2) xor d(3) xor d(5) xor d(6) xor d(7);
        next_crc(14) <= c(6) xor c(26) xor c(27) xor c(28) xor c(30) xor c(31) xor d(2) xor d(3) xor d(4) xor d(6) xor d(7);
        next_crc(15) <= c(7) xor c(27) xor c(28) xor c(29) xor c(31) xor d(3) xor d(4) xor d(5) xor d(7);
        next_crc(16) <= c(8) xor c(24) xor c(28) xor c(29) xor d(0) xor d(4) xor d(5);
        next_crc(17) <= c(9) xor c(25) xor c(29) xor c(30) xor d(1) xor d(5) xor d(6);
        next_crc(18) <= c(10) xor c(26) xor c(30) xor c(31) xor d(2) xor d(6) xor d(7);
        next_crc(19) <= c(11) xor c(27) xor c(31) xor d(3) xor d(7);
        next_crc(20) <= c(12) xor c(28) xor d(4);
        next_crc(21) <= c(13) xor c(29) xor d(5);
        next_crc(22) <= c(14) xor c(24) xor d(0);
        next_crc(23) <= c(15) xor c(24) xor c(25) xor c(30) xor d(0) xor d(1) xor d(6);
        next_crc(24) <= c(16) xor c(25) xor c(26) xor c(31) xor d(1) xor d(2) xor d(7);
        next_crc(25) <= c(17) xor c(26) xor c(27) xor d(2) xor d(3);
        next_crc(26) <= c(18) xor c(24) xor c(27) xor c(28) xor c(30) xor d(0) xor d(3) xor d(4) xor d(6);
        next_crc(27) <= c(19) xor c(25) xor c(28) xor c(29) xor c(31) xor d(1) xor d(4) xor d(5) xor d(7);
        next_crc(28) <= c(20) xor c(26) xor c(29) xor c(30) xor d(2) xor d(5) xor d(6);
        next_crc(29) <= c(21) xor c(27) xor c(30) xor c(31) xor d(3) xor d(6) xor d(7);
        next_crc(30) <= c(22) xor c(28) xor c(31) xor d(4) xor d(7);
        next_crc(31) <= c(23) xor c(29) xor d(5);
    end process;

    reg_proc: process(clk, reset)
    begin
        if reset = '1' then
            crc_reg <= (others => '1');
        elsif clk'event and clk = '1' then
            if init = '1' then
                crc_reg <= (others => '1');
            elsif enable = '1' then
                crc_reg <= next_crc;
            end if;
        end if;
    end process;

    crc_out <= not crc_reg;  -- Final inversion
end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity CRCCalculator_TB is
end CRCCalculator_TB;

architecture Test of CRCCalculator_TB is
    signal tb_clk, tb_reset, tb_init, tb_enable : bit;
    signal tb_data : bit_vector(7 downto 0);
    signal tb_crc8 : bit_vector(7 downto 0);
    signal tb_crc16 : bit_vector(15 downto 0);
    signal tb_crc32 : bit_vector(31 downto 0);
begin
    crc8_inst: entity work.CRC8
        port map (
            clk => tb_clk, reset => tb_reset, init => tb_init,
            enable => tb_enable, data_in => tb_data, crc_out => tb_crc8
        );

    crc16_inst: entity work.CRC16
        port map (
            clk => tb_clk, reset => tb_reset, init => tb_init,
            enable => tb_enable, data_in => tb_data, crc_out => tb_crc16
        );

    crc32_inst: entity work.CRC32
        port map (
            clk => tb_clk, reset => tb_reset, init => tb_init,
            enable => tb_enable, data_in => tb_data, crc_out => tb_crc32
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_init <= '0';
        tb_enable <= '0';
        tb_data <= (others => '0');
        wait for 30 ns;

        tb_reset <= '0';
        tb_init <= '1';
        wait for 10 ns;
        tb_init <= '0';

        -- Test string "123456789" (standard test vector)
        tb_enable <= '1';

        tb_data <= "00110001"; wait for 10 ns;  -- '1' = 0x31
        tb_data <= "00110010"; wait for 10 ns;  -- '2' = 0x32
        tb_data <= "00110011"; wait for 10 ns;  -- '3' = 0x33
        tb_data <= "00110100"; wait for 10 ns;  -- '4' = 0x34
        tb_data <= "00110101"; wait for 10 ns;  -- '5' = 0x35
        tb_data <= "00110110"; wait for 10 ns;  -- '6' = 0x36
        tb_data <= "00110111"; wait for 10 ns;  -- '7' = 0x37
        tb_data <= "00111000"; wait for 10 ns;  -- '8' = 0x38
        tb_data <= "00111001"; wait for 10 ns;  -- '9' = 0x39

        tb_enable <= '0';
        wait for 50 ns;

        -- CRC-32 of "123456789" should be 0xCBF43926

        wait;
    end process;
end Test;
