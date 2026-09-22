-- ============================================================================
-- VHDL Project: SequenceDetector
-- Category: State Machines
-- Complexity: Medium
-- Description: Multi-pattern sequence detector with overlapping detection
--              Configurable patterns, multiple channels
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity SequenceDetector is
    generic (
        PATTERN_LENGTH : integer := 4;
        DATA_WIDTH     : integer := 1
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        pattern     : in  bit_vector(PATTERN_LENGTH*DATA_WIDTH-1 downto 0);
        detected    : out bit;
        match_count : out bit_vector(7 downto 0);
        position    : out bit_vector(7 downto 0)  -- Where pattern started
    );
end SequenceDetector;

architecture Behavioral of SequenceDetector is
    -- Shift register to hold incoming data
    signal shift_reg : bit_vector(PATTERN_LENGTH*DATA_WIDTH-1 downto 0);
    signal count_reg : bit_vector(7 downto 0);
    signal pos_reg : bit_vector(7 downto 0);
    signal pos_counter : bit_vector(7 downto 0);
    signal prev_match : bit;

    function vectors_equal(a, b : bit_vector) return bit is
    begin
        for i in a'range loop
            if a(i) /= b(i) then
                return '0';
            end if;
        end loop;
        return '1';
    end function;

    function increment(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable carry : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := val(i) xor carry;
            carry := val(i) and carry;
        end loop;
        return result;
    end function;

begin
    detect_proc: process(clk, reset)
        variable current_match : bit;
    begin
        if reset = '1' then
            shift_reg <= (others => '0');
            count_reg <= (others => '0');
            pos_reg <= (others => '0');
            pos_counter <= (others => '0');
            prev_match <= '0';
        elsif clk'event and clk = '1' then
            if enable = '1' then
                -- Shift in new data
                shift_reg <= shift_reg(shift_reg'high-DATA_WIDTH downto 0) & data_in;

                -- Increment position counter
                pos_counter <= increment(pos_counter);

                -- Check for pattern match
                current_match := vectors_equal(shift_reg, pattern);

                -- Count matches (only on rising edge of match)
                if current_match = '1' and prev_match = '0' then
                    count_reg <= increment(count_reg);
                    -- Calculate start position
                    pos_reg <= pos_counter;
                    for i in 0 to PATTERN_LENGTH-1 loop
                        if pos_reg(0) = '0' then
                            pos_reg <= '0' & pos_reg(7 downto 1);
                        else
                            pos_reg <= '0' & pos_reg(7 downto 1);
                            pos_reg(7) <= '1';  -- Borrow logic simplified
                        end if;
                    end loop;
                end if;

                prev_match <= current_match;
            end if;
        end if;
    end process;

    detected <= vectors_equal(shift_reg, pattern);
    match_count <= count_reg;
    position <= pos_reg;

end Behavioral;

-- ============================================================================
-- Multi-Pattern Detector (4 patterns simultaneously)
-- ============================================================================
entity MultiPatternDetector is
    generic (
        PATTERN_LENGTH : integer := 8;
        NUM_PATTERNS   : integer := 4
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        data_in     : in  bit;
        -- Patterns to detect
        pattern_0   : in  bit_vector(PATTERN_LENGTH-1 downto 0);
        pattern_1   : in  bit_vector(PATTERN_LENGTH-1 downto 0);
        pattern_2   : in  bit_vector(PATTERN_LENGTH-1 downto 0);
        pattern_3   : in  bit_vector(PATTERN_LENGTH-1 downto 0);
        -- Detection outputs
        detected    : out bit_vector(NUM_PATTERNS-1 downto 0);
        any_match   : out bit;
        priority_id : out bit_vector(1 downto 0)  -- Which pattern matched (priority)
    );
end MultiPatternDetector;

architecture Behavioral of MultiPatternDetector is
    signal shift_reg : bit_vector(PATTERN_LENGTH-1 downto 0);
    signal match_flags : bit_vector(NUM_PATTERNS-1 downto 0);

    function compare(a, b : bit_vector) return bit is
    begin
        for i in a'range loop
            if a(i) /= b(i) then
                return '0';
            end if;
        end loop;
        return '1';
    end function;

begin
    shift_proc: process(clk, reset)
    begin
        if reset = '1' then
            shift_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            if enable = '1' then
                shift_reg <= shift_reg(PATTERN_LENGTH-2 downto 0) & data_in;
            end if;
        end if;
    end process;

    -- Pattern comparison (combinational)
    match_flags(0) <= compare(shift_reg, pattern_0);
    match_flags(1) <= compare(shift_reg, pattern_1);
    match_flags(2) <= compare(shift_reg, pattern_2);
    match_flags(3) <= compare(shift_reg, pattern_3);

    detected <= match_flags;
    any_match <= match_flags(0) or match_flags(1) or match_flags(2) or match_flags(3);

    -- Priority encoder
    priority_id <= "00" when match_flags(0) = '1' else
                   "01" when match_flags(1) = '1' else
                   "10" when match_flags(2) = '1' else
                   "11" when match_flags(3) = '1' else
                   "00";

end Behavioral;

-- ============================================================================
-- FSM-based Sequence Detector (handles overlapping)
-- ============================================================================
entity FSMSequenceDetector is
    port (
        clk         : in  bit;
        reset       : in  bit;
        data_in     : in  bit;
        -- Detects pattern "1011"
        detected    : out bit;
        state_out   : out bit_vector(2 downto 0)
    );
end FSMSequenceDetector;

architecture Behavioral of FSMSequenceDetector is
    type state_type is (S0, S1, S10, S101, S1011);
    signal state : state_type;
begin
    fsm_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= S0;
        elsif clk'event and clk = '1' then
            case state is
                when S0 =>
                    if data_in = '1' then
                        state <= S1;
                    else
                        state <= S0;
                    end if;

                when S1 =>
                    if data_in = '0' then
                        state <= S10;
                    else
                        state <= S1;  -- Stay for overlapping
                    end if;

                when S10 =>
                    if data_in = '1' then
                        state <= S101;
                    else
                        state <= S0;
                    end if;

                when S101 =>
                    if data_in = '1' then
                        state <= S1011;
                    else
                        state <= S10;  -- Overlapping: "10" prefix
                    end if;

                when S1011 =>
                    -- Pattern detected, handle overlapping
                    if data_in = '1' then
                        state <= S1;  -- "1" could start new pattern
                    else
                        state <= S10;  -- "10" prefix
                    end if;
            end case;
        end if;
    end process;

    detected <= '1' when state = S1011 else '0';

    with state select state_out <=
        "000" when S0,
        "001" when S1,
        "010" when S10,
        "011" when S101,
        "100" when S1011;

end Behavioral;

-- ============================================================================
-- Variable Length Sequence Detector
-- ============================================================================
entity VariableLengthDetector is
    generic (
        MAX_LENGTH : integer := 16
    );
    port (
        clk           : in  bit;
        reset         : in  bit;
        enable        : in  bit;
        data_in       : in  bit;
        pattern       : in  bit_vector(MAX_LENGTH-1 downto 0);
        pattern_len   : in  bit_vector(3 downto 0);  -- 1 to 16
        load_pattern  : in  bit;
        detected      : out bit;
        partial_match : out bit_vector(3 downto 0)  -- How many bits matched
    );
end VariableLengthDetector;

architecture Behavioral of VariableLengthDetector is
    signal shift_reg : bit_vector(MAX_LENGTH-1 downto 0);
    signal stored_pattern : bit_vector(MAX_LENGTH-1 downto 0);
    signal stored_length : bit_vector(3 downto 0);

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

    function from_integer(val : integer; width : integer) return bit_vector is
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

begin
    process(clk, reset)
        variable len : integer;
        variable match_cnt : integer;
    begin
        if reset = '1' then
            shift_reg <= (others => '0');
            stored_pattern <= (others => '0');
            stored_length <= "0001";  -- Minimum length 1
        elsif clk'event and clk = '1' then
            if load_pattern = '1' then
                stored_pattern <= pattern;
                stored_length <= pattern_len;
            end if;

            if enable = '1' then
                shift_reg <= shift_reg(MAX_LENGTH-2 downto 0) & data_in;
            end if;
        end if;
    end process;

    -- Detection logic
    detect_proc: process(shift_reg, stored_pattern, stored_length)
        variable len : integer;
        variable match_cnt : integer;
        variable all_match : bit;
    begin
        len := to_integer(stored_length);
        if len = 0 then len := 1; end if;
        if len > MAX_LENGTH then len := MAX_LENGTH; end if;

        -- Count matching bits
        match_cnt := 0;
        all_match := '1';

        for i in 0 to MAX_LENGTH-1 loop
            if i < len then
                if shift_reg(i) = stored_pattern(i) then
                    match_cnt := match_cnt + 1;
                else
                    all_match := '0';
                end if;
            end if;
        end loop;

        detected <= all_match;
        partial_match <= from_integer(match_cnt, 4);
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity SequenceDetector_TB is
end SequenceDetector_TB;

architecture Test of SequenceDetector_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_data : bit;
    signal tb_detected : bit;
    signal tb_state : bit_vector(2 downto 0);
begin
    UUT: entity work.FSMSequenceDetector
        port map (
            clk       => tb_clk,
            reset     => tb_reset,
            data_in   => tb_data,
            detected  => tb_detected,
            state_out => tb_state
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_data <= '0';
        wait for 30 ns;

        tb_reset <= '0';

        -- Input sequence: 0 1 0 1 1 0 1 1 (contains "1011" twice with overlap)
        tb_data <= '0'; wait for 10 ns;
        tb_data <= '1'; wait for 10 ns;
        tb_data <= '0'; wait for 10 ns;
        tb_data <= '1'; wait for 10 ns;
        tb_data <= '1'; wait for 10 ns;  -- First detection
        tb_data <= '0'; wait for 10 ns;
        tb_data <= '1'; wait for 10 ns;
        tb_data <= '1'; wait for 10 ns;  -- Second detection (overlapping)

        wait for 50 ns;
        wait;
    end process;
end Test;
