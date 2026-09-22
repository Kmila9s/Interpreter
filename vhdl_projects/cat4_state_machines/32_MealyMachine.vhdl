-- ============================================================================
-- VHDL Project: MealyMachine
-- Category: State Machines
-- Complexity: Medium
-- Description: Mealy FSM where output depends on state AND input
--              Includes registered output variant
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity MealyMachine is
    generic (
        STATE_BITS : integer := 3;
        INPUT_BITS : integer := 2;
        OUTPUT_BITS : integer := 4
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        input_data  : in  bit_vector(INPUT_BITS-1 downto 0);
        output_data : out bit_vector(OUTPUT_BITS-1 downto 0);
        output_reg  : out bit_vector(OUTPUT_BITS-1 downto 0);  -- Registered output
        state_out   : out bit_vector(STATE_BITS-1 downto 0)
    );
end MealyMachine;

architecture Behavioral of MealyMachine is
    constant NUM_STATES : integer := 2 ** STATE_BITS;
    constant NUM_INPUTS : integer := 2 ** INPUT_BITS;

    signal current_state : bit_vector(STATE_BITS-1 downto 0);
    signal next_state : bit_vector(STATE_BITS-1 downto 0);
    signal mealy_output : bit_vector(OUTPUT_BITS-1 downto 0);
    signal registered_output : bit_vector(OUTPUT_BITS-1 downto 0);

    -- Transition table
    type transition_row is array (0 to NUM_INPUTS-1) of bit_vector(STATE_BITS-1 downto 0);
    type transition_table_type is array (0 to NUM_STATES-1) of transition_row;

    -- Output table (Mealy: state x input -> output)
    type output_row is array (0 to NUM_INPUTS-1) of bit_vector(OUTPUT_BITS-1 downto 0);
    type output_table_type is array (0 to NUM_STATES-1) of output_row;

    -- Example: Overlapping sequence detector (101)
    constant TRANS_TABLE : transition_table_type := (
        -- State 0 (IDLE): looking for 1
        (0 => "000", 1 => "001", 2 => "000", 3 => "000"),
        -- State 1 (GOT_1): got 1, looking for 0
        (0 => "010", 1 => "001", 2 => "000", 3 => "000"),
        -- State 2 (GOT_10): got 10, looking for 1
        (0 => "000", 1 => "001", 2 => "000", 3 => "000"),  -- Note: on 1, detect and go to GOT_1
        others => (others => "000")
    );

    constant OUTPUT_TABLE : output_table_type := (
        -- State 0: no detection
        (0 => "0000", 1 => "0000", 2 => "0000", 3 => "0000"),
        -- State 1: no detection
        (0 => "0000", 1 => "0000", 2 => "0000", 3 => "0000"),
        -- State 2: detect on input=1
        (0 => "0000", 1 => "1111", 2 => "0000", 3 => "0000"),  -- Output 1111 when 101 detected
        others => (others => "0000")
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
    -- State register
    state_reg: process(clk, reset)
    begin
        if reset = '1' then
            current_state <= (others => '0');
            registered_output <= (others => '0');
        elsif clk'event and clk = '1' then
            if enable = '1' then
                current_state <= next_state;
                registered_output <= mealy_output;
            end if;
        end if;
    end process;

    -- Next state and output logic (combinational)
    mealy_logic: process(current_state, input_data)
        variable state_idx : integer;
        variable input_idx : integer;
    begin
        state_idx := to_integer(current_state);
        input_idx := to_integer(input_data);

        if state_idx < NUM_STATES and input_idx < NUM_INPUTS then
            next_state <= TRANS_TABLE(state_idx)(input_idx);
            mealy_output <= OUTPUT_TABLE(state_idx)(input_idx);
        else
            next_state <= (others => '0');
            mealy_output <= (others => '0');
        end if;
    end process;

    output_data <= mealy_output;
    output_reg <= registered_output;
    state_out <= current_state;

end Behavioral;

-- ============================================================================
-- Mealy Machine: Serial Bit Adder
-- ============================================================================
entity SerialAdder is
    port (
        clk         : in  bit;
        reset       : in  bit;
        bit_a       : in  bit;
        bit_b       : in  bit;
        sum_out     : out bit;
        carry_out   : out bit
    );
end SerialAdder;

architecture Mealy of SerialAdder is
    type state_type is (NO_CARRY, CARRY);
    signal state : state_type;
begin
    process(clk, reset)
    begin
        if reset = '1' then
            state <= NO_CARRY;
        elsif clk'event and clk = '1' then
            case state is
                when NO_CARRY =>
                    if (bit_a and bit_b) = '1' then
                        state <= CARRY;
                    end if;

                when CARRY =>
                    if (bit_a or bit_b) = '0' then
                        state <= NO_CARRY;
                    end if;
            end case;
        end if;
    end process;

    -- Mealy outputs (depend on state AND inputs)
    process(state, bit_a, bit_b)
        variable input_sum : integer;
    begin
        -- Count input 1s
        input_sum := 0;
        if bit_a = '1' then input_sum := input_sum + 1; end if;
        if bit_b = '1' then input_sum := input_sum + 1; end if;
        if state = CARRY then input_sum := input_sum + 1; end if;

        -- Sum bit
        if input_sum = 1 or input_sum = 3 then
            sum_out <= '1';
        else
            sum_out <= '0';
        end if;

        -- Carry output
        if input_sum >= 2 then
            carry_out <= '1';
        else
            carry_out <= '0';
        end if;
    end process;

end Mealy;

-- ============================================================================
-- Mealy Machine: Edge-Triggered Counter Control
-- ============================================================================
entity CounterControl is
    port (
        clk         : in  bit;
        reset       : in  bit;
        up_btn      : in  bit;
        down_btn    : in  bit;
        count_en    : out bit;
        count_dir   : out bit;  -- '0' = down, '1' = up
        count_pulse : out bit   -- Single pulse on edge
    );
end CounterControl;

architecture Mealy of CounterControl is
    type state_type is (IDLE, UP_PRESSED, UP_HELD, DOWN_PRESSED, DOWN_HELD);
    signal state : state_type;
begin
    fsm_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE;
        elsif clk'event and clk = '1' then
            case state is
                when IDLE =>
                    if up_btn = '1' then
                        state <= UP_PRESSED;
                    elsif down_btn = '1' then
                        state <= DOWN_PRESSED;
                    end if;

                when UP_PRESSED =>
                    state <= UP_HELD;

                when UP_HELD =>
                    if up_btn = '0' then
                        if down_btn = '1' then
                            state <= DOWN_PRESSED;
                        else
                            state <= IDLE;
                        end if;
                    end if;

                when DOWN_PRESSED =>
                    state <= DOWN_HELD;

                when DOWN_HELD =>
                    if down_btn = '0' then
                        if up_btn = '1' then
                            state <= UP_PRESSED;
                        else
                            state <= IDLE;
                        end if;
                    end if;
            end case;
        end if;
    end process;

    -- Mealy outputs
    count_en <= '1' when (state = UP_PRESSED or state = UP_HELD or
                          state = DOWN_PRESSED or state = DOWN_HELD) else '0';

    count_dir <= '1' when (state = UP_PRESSED or state = UP_HELD) else '0';

    -- Pulse only on transition states (Mealy behavior)
    count_pulse <= '1' when (state = UP_PRESSED or state = DOWN_PRESSED) else '0';

end Mealy;

-- ============================================================================
-- Mealy Machine: Protocol Handshake
-- ============================================================================
entity HandshakeFSM is
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Request side
        req_valid   : in  bit;
        req_data    : in  bit_vector(7 downto 0);
        req_ready   : out bit;
        -- Response side
        resp_valid  : out bit;
        resp_data   : out bit_vector(7 downto 0);
        resp_ready  : in  bit;
        -- Status
        busy        : out bit;
        error       : out bit
    );
end HandshakeFSM;

architecture Mealy of HandshakeFSM is
    type state_type is (IDLE, WAIT_ACCEPT, PROCESSING, WAIT_RESPONSE, ERROR_STATE);
    signal state : state_type;
    signal data_reg : bit_vector(7 downto 0);
    signal timeout_cnt : integer range 0 to 255;
begin
    fsm_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE;
            data_reg <= (others => '0');
            timeout_cnt <= 0;
        elsif clk'event and clk = '1' then
            case state is
                when IDLE =>
                    timeout_cnt <= 0;
                    if req_valid = '1' then
                        data_reg <= req_data;
                        state <= WAIT_ACCEPT;
                    end if;

                when WAIT_ACCEPT =>
                    -- Immediate transition for Mealy machine
                    state <= PROCESSING;

                when PROCESSING =>
                    -- Simulate processing
                    state <= WAIT_RESPONSE;

                when WAIT_RESPONSE =>
                    if resp_ready = '1' then
                        state <= IDLE;
                    else
                        timeout_cnt <= timeout_cnt + 1;
                        if timeout_cnt >= 200 then
                            state <= ERROR_STATE;
                        end if;
                    end if;

                when ERROR_STATE =>
                    if req_valid = '0' then
                        state <= IDLE;
                    end if;
            end case;
        end if;
    end process;

    -- Mealy outputs (combinational, depend on state and inputs)
    -- Ready to accept new request
    req_ready <= '1' when (state = IDLE and req_valid = '1') or
                          (state = WAIT_ACCEPT) else '0';

    -- Response valid when waiting and data ready
    resp_valid <= '1' when state = WAIT_RESPONSE else '0';

    resp_data <= data_reg;

    busy <= '0' when state = IDLE else '1';
    error <= '1' when state = ERROR_STATE else '0';

end Mealy;

-- ============================================================================
-- Testbench
-- ============================================================================
entity MealyMachine_TB is
end MealyMachine_TB;

architecture Test of MealyMachine_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_bit_a, tb_bit_b : bit;
    signal tb_sum, tb_carry : bit;
begin
    UUT: entity work.SerialAdder
        port map (
            clk       => tb_clk,
            reset     => tb_reset,
            bit_a     => tb_bit_a,
            bit_b     => tb_bit_b,
            sum_out   => tb_sum,
            carry_out => tb_carry
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_bit_a <= '0';
        tb_bit_b <= '0';
        wait for 30 ns;

        tb_reset <= '0';

        -- Add 5 (101) + 3 (011) = 8 (1000), LSB first
        -- Bit 0: 1 + 1 = 0, carry
        tb_bit_a <= '1'; tb_bit_b <= '1'; wait for 10 ns;
        -- Bit 1: 0 + 1 + carry = 0, carry
        tb_bit_a <= '0'; tb_bit_b <= '1'; wait for 10 ns;
        -- Bit 2: 1 + 0 + carry = 0, carry
        tb_bit_a <= '1'; tb_bit_b <= '0'; wait for 10 ns;
        -- Bit 3: 0 + 0 + carry = 1, no carry
        tb_bit_a <= '0'; tb_bit_b <= '0'; wait for 10 ns;

        wait for 50 ns;
        wait;
    end process;
end Test;
