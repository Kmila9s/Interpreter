-- ============================================================================
-- VHDL Project: MooreMachine
-- Category: State Machines
-- Complexity: Low-Medium
-- Description: Generic Moore FSM with configurable states
--              Output depends only on current state
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity MooreMachine is
    generic (
        STATE_BITS : integer := 4;
        INPUT_BITS : integer := 2;
        OUTPUT_BITS : integer := 4
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        input_data  : in  bit_vector(INPUT_BITS-1 downto 0);
        output_data : out bit_vector(OUTPUT_BITS-1 downto 0);
        state_out   : out bit_vector(STATE_BITS-1 downto 0)
    );
end MooreMachine;

architecture Behavioral of MooreMachine is
    constant NUM_STATES : integer := 2 ** STATE_BITS;
    constant NUM_INPUTS : integer := 2 ** INPUT_BITS;

    signal current_state : bit_vector(STATE_BITS-1 downto 0);
    signal next_state : bit_vector(STATE_BITS-1 downto 0);

    -- Transition table: state x input -> next_state
    type transition_row is array (0 to NUM_INPUTS-1) of bit_vector(STATE_BITS-1 downto 0);
    type transition_table_type is array (0 to NUM_STATES-1) of transition_row;

    -- Output table: state -> output
    type output_table_type is array (0 to NUM_STATES-1) of bit_vector(OUTPUT_BITS-1 downto 0);

    -- Example: Simple sequence detector (0110)
    -- States: IDLE=0, S0=1, S01=2, S011=3, DETECTED=4
    constant TRANS_TABLE : transition_table_type := (
        -- State 0 (IDLE): looking for first 0
        (0 => "0001", 1 => "0000", 2 => "0000", 3 => "0000"),
        -- State 1 (S0): got 0, looking for 1
        (0 => "0001", 1 => "0010", 2 => "0000", 3 => "0000"),
        -- State 2 (S01): got 01, looking for 1
        (0 => "0001", 1 => "0011", 2 => "0000", 3 => "0000"),
        -- State 3 (S011): got 011, looking for 0
        (0 => "0100", 1 => "0000", 2 => "0000", 3 => "0000"),
        -- State 4 (DETECTED): pattern found
        (0 => "0001", 1 => "0000", 2 => "0000", 3 => "0000"),
        -- Remaining states
        others => (others => "0000")
    );

    constant OUTPUT_TABLE : output_table_type := (
        0 => "0000",  -- IDLE
        1 => "0001",  -- S0
        2 => "0010",  -- S01
        3 => "0011",  -- S011
        4 => "1111",  -- DETECTED
        others => "0000"
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
        elsif clk'event and clk = '1' then
            if enable = '1' then
                current_state <= next_state;
            end if;
        end if;
    end process;

    -- Next state logic (combinational)
    next_state_logic: process(current_state, input_data)
        variable state_idx : integer;
        variable input_idx : integer;
    begin
        state_idx := to_integer(current_state);
        input_idx := to_integer(input_data);

        if state_idx < NUM_STATES and input_idx < NUM_INPUTS then
            next_state <= TRANS_TABLE(state_idx)(input_idx);
        else
            next_state <= (others => '0');
        end if;
    end process;

    -- Output logic (Moore: depends only on state)
    output_logic: process(current_state)
        variable state_idx : integer;
    begin
        state_idx := to_integer(current_state);
        if state_idx < NUM_STATES then
            output_data <= OUTPUT_TABLE(state_idx);
        else
            output_data <= (others => '0');
        end if;
    end process;

    state_out <= current_state;

end Behavioral;

-- ============================================================================
-- Moore Machine with One-Hot Encoding
-- ============================================================================
entity MooreOneHot is
    port (
        clk         : in  bit;
        reset       : in  bit;
        input_a     : in  bit;
        input_b     : in  bit;
        output_x    : out bit;
        output_y    : out bit;
        output_z    : out bit
    );
end MooreOneHot;

architecture Behavioral of MooreOneHot is
    -- One-hot state encoding
    signal state : bit_vector(4 downto 0);

    -- State aliases
    constant S_IDLE    : bit_vector(4 downto 0) := "00001";
    constant S_START   : bit_vector(4 downto 0) := "00010";
    constant S_PROCESS : bit_vector(4 downto 0) := "00100";
    constant S_WAIT    : bit_vector(4 downto 0) := "01000";
    constant S_DONE    : bit_vector(4 downto 0) := "10000";

begin
    fsm_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= S_IDLE;
        elsif clk'event and clk = '1' then
            case state is
                when S_IDLE =>
                    if input_a = '1' then
                        state <= S_START;
                    end if;

                when S_START =>
                    if input_b = '1' then
                        state <= S_PROCESS;
                    else
                        state <= S_WAIT;
                    end if;

                when S_PROCESS =>
                    state <= S_DONE;

                when S_WAIT =>
                    if input_a = '1' and input_b = '1' then
                        state <= S_PROCESS;
                    elsif input_a = '0' then
                        state <= S_IDLE;
                    end if;

                when S_DONE =>
                    state <= S_IDLE;

                when others =>
                    state <= S_IDLE;
            end case;
        end if;
    end process;

    -- Moore outputs (registered style for clean timing)
    output_x <= state(2) or state(4);  -- Active in PROCESS or DONE
    output_y <= state(1) or state(2);  -- Active in START or PROCESS
    output_z <= state(4);               -- Active only in DONE

end Behavioral;

-- ============================================================================
-- Moore Machine with Safe State Encoding
-- ============================================================================
entity MooreSafe is
    port (
        clk         : in  bit;
        reset       : in  bit;
        go          : in  bit;
        stop        : in  bit;
        status      : out bit_vector(2 downto 0);
        running     : out bit;
        error       : out bit
    );
end MooreSafe;

architecture Behavioral of MooreSafe is
    type state_type is (IDLE, STARTING, RUNNING, STOPPING, ERROR_STATE);
    signal state : state_type;
    signal state_code : bit_vector(2 downto 0);

    -- Watchdog for detecting stuck states
    signal watchdog_counter : integer range 0 to 1023;
    constant WATCHDOG_LIMIT : integer := 1000;

begin
    fsm_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE;
            watchdog_counter <= 0;
        elsif clk'event and clk = '1' then
            -- Watchdog logic
            if state = STARTING or state = STOPPING then
                if watchdog_counter >= WATCHDOG_LIMIT then
                    state <= ERROR_STATE;
                    watchdog_counter <= 0;
                else
                    watchdog_counter <= watchdog_counter + 1;
                end if;
            else
                watchdog_counter <= 0;
            end if;

            -- State transitions
            case state is
                when IDLE =>
                    if go = '1' then
                        state <= STARTING;
                    end if;

                when STARTING =>
                    if stop = '1' then
                        state <= STOPPING;
                    else
                        state <= RUNNING;
                    end if;

                when RUNNING =>
                    if stop = '1' then
                        state <= STOPPING;
                    end if;

                when STOPPING =>
                    state <= IDLE;

                when ERROR_STATE =>
                    if go = '0' and stop = '0' then
                        state <= IDLE;  -- Recovery
                    end if;

            end case;
        end if;
    end process;

    -- State encoding output
    with state select state_code <=
        "000" when IDLE,
        "001" when STARTING,
        "010" when RUNNING,
        "011" when STOPPING,
        "111" when ERROR_STATE;

    status <= state_code;
    running <= '1' when state = RUNNING else '0';
    error <= '1' when state = ERROR_STATE else '0';

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity MooreMachine_TB is
end MooreMachine_TB;

architecture Test of MooreMachine_TB is
    signal tb_clk, tb_reset, tb_enable : bit;
    signal tb_input : bit_vector(1 downto 0);
    signal tb_output : bit_vector(3 downto 0);
    signal tb_state : bit_vector(3 downto 0);
begin
    UUT: entity work.MooreMachine
        generic map (STATE_BITS => 4, INPUT_BITS => 2, OUTPUT_BITS => 4)
        port map (
            clk         => tb_clk,
            reset       => tb_reset,
            enable      => tb_enable,
            input_data  => tb_input,
            output_data => tb_output,
            state_out   => tb_state
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_enable <= '0';
        tb_input <= "00";
        wait for 30 ns;

        tb_reset <= '0';
        tb_enable <= '1';

        -- Input sequence to detect 0110
        tb_input <= "00"; wait for 10 ns;  -- 0
        tb_input <= "01"; wait for 10 ns;  -- 1
        tb_input <= "01"; wait for 10 ns;  -- 1
        tb_input <= "00"; wait for 10 ns;  -- 0 - pattern complete

        wait for 50 ns;
        wait;
    end process;
end Test;
