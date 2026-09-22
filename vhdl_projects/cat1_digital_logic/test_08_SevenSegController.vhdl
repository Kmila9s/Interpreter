-- ============================================================================
-- VHDL Project: SevenSegController
-- Category: Digital Logic Fundamentals
-- Complexity: Medium
-- Description: Multi-digit 7-segment display controller
--              Features: multiplexed display, brightness control (PWM),
--              decimal point support, blanking, hexadecimal/BCD mode
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity SevenSegController is
    generic (
        NUM_DIGITS      : integer := 4;
        CLK_FREQ_KHZ    : integer := 50000;  -- 50 MHz
        REFRESH_RATE_HZ : integer := 1000     -- 1 kHz refresh per digit
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Data inputs (one nibble per digit)
        digit_0         : in  bit_vector(3 downto 0);
        digit_1         : in  bit_vector(3 downto 0);
        digit_2         : in  bit_vector(3 downto 0);
        digit_3         : in  bit_vector(3 downto 0);
        -- Control inputs
        decimal_points  : in  bit_vector(NUM_DIGITS-1 downto 0);
        blank_digits    : in  bit_vector(NUM_DIGITS-1 downto 0);
        hex_mode        : in  bit;  -- '1' for hex (0-F), '0' for decimal (0-9)
        brightness      : in  bit_vector(2 downto 0);  -- PWM duty cycle
        -- Outputs to display
        segments        : out bit_vector(7 downto 0);  -- a,b,c,d,e,f,g,dp
        digit_select    : out bit_vector(NUM_DIGITS-1 downto 0)  -- Active digit
    );
end SevenSegController;

architecture Behavioral of SevenSegController is
    -- Clock divider for refresh rate
    constant REFRESH_DIVIDER : integer := (CLK_FREQ_KHZ * 1000) / (REFRESH_RATE_HZ * NUM_DIGITS);
    signal refresh_counter : integer range 0 to REFRESH_DIVIDER-1;
    signal digit_counter : integer range 0 to NUM_DIGITS-1;

    -- PWM for brightness
    constant PWM_PERIOD : integer := 8;
    signal pwm_counter : integer range 0 to PWM_PERIOD-1;
    signal pwm_duty : integer range 0 to PWM_PERIOD;
    signal pwm_on : bit;

    -- Current digit data
    signal current_digit : bit_vector(3 downto 0);
    signal current_dp : bit;
    signal current_blank : bit;
    signal segment_pattern : bit_vector(6 downto 0);

    -- 7-segment encoding (active HIGH: a=bit6, b=bit5, ... g=bit0)
    --     aaa
    --    f   b
    --     ggg
    --    e   c
    --     ddd

    function hex_to_7seg(val : bit_vector(3 downto 0)) return bit_vector is
        variable seg : bit_vector(6 downto 0);
    begin
        case val is
            when "0000" => seg := "1111110";  -- 0
            when "0001" => seg := "0110000";  -- 1
            when "0010" => seg := "1101101";  -- 2
            when "0011" => seg := "1111001";  -- 3
            when "0100" => seg := "0110011";  -- 4
            when "0101" => seg := "1011011";  -- 5
            when "0110" => seg := "1011111";  -- 6
            when "0111" => seg := "1110000";  -- 7
            when "1000" => seg := "1111111";  -- 8
            when "1001" => seg := "1111011";  -- 9
            when "1010" => seg := "1110111";  -- A
            when "1011" => seg := "0011111";  -- b
            when "1100" => seg := "1001110";  -- C
            when "1101" => seg := "0111101";  -- d
            when "1110" => seg := "1001111";  -- E
            when "1111" => seg := "1000111";  -- F
            when others => seg := "0000000";
        end case;
        return seg;
    end function;

    function bcd_to_7seg(val : bit_vector(3 downto 0)) return bit_vector is
        variable seg : bit_vector(6 downto 0);
    begin
        case val is
            when "0000" => seg := "1111110";  -- 0
            when "0001" => seg := "0110000";  -- 1
            when "0010" => seg := "1101101";  -- 2
            when "0011" => seg := "1111001";  -- 3
            when "0100" => seg := "0110011";  -- 4
            when "0101" => seg := "1011011";  -- 5
            when "0110" => seg := "1011111";  -- 6
            when "0111" => seg := "1110000";  -- 7
            when "1000" => seg := "1111111";  -- 8
            when "1001" => seg := "1111011";  -- 9
            when others => seg := "0000001";  -- Error indicator (-)
        end case;
        return seg;
    end function;

    function bits_to_int(b : bit_vector(2 downto 0)) return integer is
        variable result : integer := 0;
    begin
        if b(0) = '1' then result := result + 1; end if;
        if b(1) = '1' then result := result + 2; end if;
        if b(2) = '1' then result := result + 4; end if;
        return result;
    end function;

begin
    -- Refresh rate divider and digit multiplexing
    refresh_process: process(clk, reset)
    begin
        if reset = '1' then
            refresh_counter <= 0;
            digit_counter <= 0;
        elsif clk'event and clk = '1' then
            if refresh_counter = REFRESH_DIVIDER - 1 then
                refresh_counter <= 0;
                if digit_counter = NUM_DIGITS - 1 then
                    digit_counter <= 0;
                else
                    digit_counter <= digit_counter + 1;
                end if;
            else
                refresh_counter <= refresh_counter + 1;
            end if;
        end if;
    end process refresh_process;

    -- PWM generation for brightness
    pwm_duty <= bits_to_int(brightness) + 1;  -- 1-8

    pwm_process: process(clk, reset)
    begin
        if reset = '1' then
            pwm_counter <= 0;
            pwm_on <= '1';
        elsif clk'event and clk = '1' then
            if pwm_counter = PWM_PERIOD - 1 then
                pwm_counter <= 0;
            else
                pwm_counter <= pwm_counter + 1;
            end if;

            if pwm_counter < pwm_duty then
                pwm_on <= '1';
            else
                pwm_on <= '0';
            end if;
        end if;
    end process pwm_process;

    -- Select current digit data
    digit_select_process: process(digit_counter, digit_0, digit_1, digit_2, digit_3,
                                  decimal_points, blank_digits)
    begin
        case digit_counter is
            when 0 =>
                current_digit <= digit_0;
                current_dp <= decimal_points(0);
                current_blank <= blank_digits(0);
            when 1 =>
                current_digit <= digit_1;
                current_dp <= decimal_points(1);
                current_blank <= blank_digits(1);
            when 2 =>
                current_digit <= digit_2;
                current_dp <= decimal_points(2);
                current_blank <= blank_digits(2);
            when 3 =>
                current_digit <= digit_3;
                current_dp <= decimal_points(3);
                current_blank <= blank_digits(3);
            when others =>
                current_digit <= "0000";
                current_dp <= '0';
                current_blank <= '1';
        end case;
    end process digit_select_process;

    -- Generate segment pattern
    segment_pattern <= hex_to_7seg(current_digit) when hex_mode = '1'
                       else bcd_to_7seg(current_digit);

    -- Output segments with blanking and PWM
    segment_output: process(segment_pattern, current_dp, current_blank, pwm_on)
    begin
        if current_blank = '1' or pwm_on = '0' then
            segments <= "00000000";
        else
            segments(7) <= current_dp;
            segments(6 downto 0) <= segment_pattern;
        end if;
    end process segment_output;

    -- Digit select output (active high, one-hot)
    digit_select_output: process(digit_counter, pwm_on)
    begin
        digit_select <= (others => '0');
        if pwm_on = '1' then
            case digit_counter is
                when 0 => digit_select(0) <= '1';
                when 1 => digit_select(1) <= '1';
                when 2 => digit_select(2) <= '1';
                when 3 => digit_select(3) <= '1';
                when others => digit_select <= (others => '0');
            end case;
        end if;
    end process digit_select_output;

end Behavioral;

-- ============================================================================
-- Single 7-Segment Decoder
-- ============================================================================
entity SevenSegDecoder is
    port (
        value       : in  bit_vector(3 downto 0);
        hex_mode    : in  bit;
        active_low  : in  bit;
        segments    : out bit_vector(6 downto 0)  -- a,b,c,d,e,f,g
    );
end SevenSegDecoder;

architecture Behavioral of SevenSegDecoder is
    signal seg_active_high : bit_vector(6 downto 0);
begin
    decode: process(value, hex_mode)
    begin
        if hex_mode = '1' then
            case value is
                when "0000" => seg_active_high <= "1111110";
                when "0001" => seg_active_high <= "0110000";
                when "0010" => seg_active_high <= "1101101";
                when "0011" => seg_active_high <= "1111001";
                when "0100" => seg_active_high <= "0110011";
                when "0101" => seg_active_high <= "1011011";
                when "0110" => seg_active_high <= "1011111";
                when "0111" => seg_active_high <= "1110000";
                when "1000" => seg_active_high <= "1111111";
                when "1001" => seg_active_high <= "1111011";
                when "1010" => seg_active_high <= "1110111";
                when "1011" => seg_active_high <= "0011111";
                when "1100" => seg_active_high <= "1001110";
                when "1101" => seg_active_high <= "0111101";
                when "1110" => seg_active_high <= "1001111";
                when "1111" => seg_active_high <= "1000111";
                when others => seg_active_high <= "0000000";
            end case;
        else
            case value is
                when "0000" => seg_active_high <= "1111110";
                when "0001" => seg_active_high <= "0110000";
                when "0010" => seg_active_high <= "1101101";
                when "0011" => seg_active_high <= "1111001";
                when "0100" => seg_active_high <= "0110011";
                when "0101" => seg_active_high <= "1011011";
                when "0110" => seg_active_high <= "1011111";
                when "0111" => seg_active_high <= "1110000";
                when "1000" => seg_active_high <= "1111111";
                when "1001" => seg_active_high <= "1111011";
                when others => seg_active_high <= "0000001";  -- Error: dash
            end case;
        end if;
    end process decode;

    -- Apply active level
    output_gen: for i in 0 to 6 generate
        segments(i) <= seg_active_high(i) xor active_low;
    end generate output_gen;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity SevenSegController_TB is
end SevenSegController_TB;

architecture Test of SevenSegController_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_digit_0, tb_digit_1, tb_digit_2, tb_digit_3 : bit_vector(3 downto 0);
    signal tb_decimal_points, tb_blank_digits : bit_vector(3 downto 0);
    signal tb_hex_mode : bit;
    signal tb_brightness : bit_vector(2 downto 0);
    signal tb_segments : bit_vector(7 downto 0);
    signal tb_digit_select : bit_vector(3 downto 0);
begin
    UUT: entity work.SevenSegController
        generic map (
            NUM_DIGITS => 4,
            CLK_FREQ_KHZ => 1000,
            REFRESH_RATE_HZ => 100
        )
        port map (
            clk            => tb_clk,
            reset          => tb_reset,
            digit_0        => tb_digit_0,
            digit_1        => tb_digit_1,
            digit_2        => tb_digit_2,
            digit_3        => tb_digit_3,
            decimal_points => tb_decimal_points,
            blank_digits   => tb_blank_digits,
            hex_mode       => tb_hex_mode,
            brightness     => tb_brightness,
            segments       => tb_segments,
            digit_select   => tb_digit_select
        );

    -- Clock generation
    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_digit_0 <= "0001";  -- 1
        tb_digit_1 <= "0010";  -- 2
        tb_digit_2 <= "0011";  -- 3
        tb_digit_3 <= "0100";  -- 4
        tb_decimal_points <= "0010";  -- DP on digit 1
        tb_blank_digits <= "0000";
        tb_hex_mode <= '0';
        tb_brightness <= "111";  -- Full brightness
        wait for 50 ns;

        tb_reset <= '0';
        wait for 5000 ns;

        -- Test hex mode
        tb_hex_mode <= '1';
        tb_digit_0 <= "1010";  -- A
        tb_digit_1 <= "1011";  -- b
        tb_digit_2 <= "1100";  -- C
        tb_digit_3 <= "1101";  -- d
        wait for 5000 ns;

        -- Test brightness levels
        tb_brightness <= "011";
        wait for 2000 ns;
        tb_brightness <= "001";
        wait for 2000 ns;

        wait;
    end process stimulus;
end Test;
