-- ============================================================================
-- VHDL Project: PhaseDetector
-- Category: Timing & Synchronization
-- Complexity: High
-- Description: Phase and frequency detector for PLL applications
--              Supports multiple detection methods: XOR, flip-flop based,
--              digital PFD with charge pump interface
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity PhaseDetector is
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Reference and feedback signals
        ref_clk     : in  bit;  -- Reference clock
        fb_clk      : in  bit;  -- Feedback clock
        -- Detection mode
        mode        : in  bit_vector(1 downto 0);
        -- "00" = XOR (simple phase detector)
        -- "01" = Edge-triggered PFD
        -- "10" = Frequency detector only
        -- "11" = Combined PFD
        -- Outputs
        phase_error : out bit_vector(15 downto 0);  -- Signed phase error
        up          : out bit;  -- Increase frequency
        down        : out bit;  -- Decrease frequency
        locked      : out bit;  -- Phase locked indication
        -- Charge pump interface
        pump_up     : out bit;  -- Charge pump up pulse
        pump_down   : out bit   -- Charge pump down pulse
    );
end PhaseDetector;

architecture Behavioral of PhaseDetector is
    -- Synchronization
    signal ref_sync1, ref_sync2 : bit;
    signal fb_sync1, fb_sync2 : bit;
    signal ref_synced, fb_synced : bit;
    signal ref_prev, fb_prev : bit;

    -- Edge detection
    signal ref_rising, fb_rising : bit;
    signal ref_falling, fb_falling : bit;

    -- XOR phase detector
    signal xor_output : bit;

    -- PFD flip-flops
    signal up_ff, down_ff : bit;
    signal reset_pfd : bit;

    -- Phase error measurement
    signal phase_counter : integer range -32768 to 32767;
    signal measuring : bit;
    signal error_reg : bit_vector(15 downto 0);

    -- Lock detection
    signal lock_counter : integer range 0 to 255;
    signal locked_reg : bit;
    constant LOCK_THRESHOLD : integer := 200;

    function to_bitvector_signed(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer;
    begin
        if val < 0 then
            temp := val + 2**width;  -- Two's complement
        else
            temp := val;
        end if;
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
    -- Input synchronization
    sync_proc: process(clk, reset)
    begin
        if reset = '1' then
            ref_sync1 <= '0';
            ref_sync2 <= '0';
            fb_sync1 <= '0';
            fb_sync2 <= '0';
        elsif clk'event and clk = '1' then
            ref_sync1 <= ref_clk;
            ref_sync2 <= ref_sync1;
            fb_sync1 <= fb_clk;
            fb_sync2 <= fb_sync1;
        end if;
    end process sync_proc;

    ref_synced <= ref_sync2;
    fb_synced <= fb_sync2;

    -- Edge detection
    edge_proc: process(clk, reset)
    begin
        if reset = '1' then
            ref_prev <= '0';
            fb_prev <= '0';
        elsif clk'event and clk = '1' then
            ref_prev <= ref_synced;
            fb_prev <= fb_synced;
        end if;
    end process edge_proc;

    ref_rising <= ref_synced and (not ref_prev);
    fb_rising <= fb_synced and (not fb_prev);
    ref_falling <= (not ref_synced) and ref_prev;
    fb_falling <= (not fb_synced) and fb_prev;

    -- XOR phase detector
    xor_output <= ref_synced xor fb_synced;

    -- PFD (Phase-Frequency Detector) with flip-flops
    pfd_proc: process(clk, reset)
    begin
        if reset = '1' then
            up_ff <= '0';
            down_ff <= '0';
        elsif clk'event and clk = '1' then
            -- Reset both FFs when both are high (prevents dead zone)
            reset_pfd <= up_ff and down_ff;

            if reset_pfd = '1' then
                up_ff <= '0';
                down_ff <= '0';
            else
                -- Reference edge sets UP
                if ref_rising = '1' then
                    up_ff <= '1';
                end if;

                -- Feedback edge sets DOWN
                if fb_rising = '1' then
                    down_ff <= '1';
                end if;
            end if;
        end if;
    end process pfd_proc;

    -- Phase error measurement
    phase_measure_proc: process(clk, reset)
    begin
        if reset = '1' then
            phase_counter <= 0;
            measuring <= '0';
            error_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            case mode is
                when "00" =>
                    -- XOR mode: simple up/down based on XOR output
                    if xor_output = '1' then
                        if ref_synced = '1' then
                            -- Reference leads
                            phase_counter <= phase_counter + 1;
                        else
                            -- Feedback leads
                            phase_counter <= phase_counter - 1;
                        end if;
                    end if;

                    -- Capture error on reference edge
                    if ref_falling = '1' then
                        error_reg <= to_bitvector_signed(phase_counter, 16);
                        phase_counter <= 0;
                    end if;

                when "01" | "11" =>
                    -- PFD mode
                    if up_ff = '1' and down_ff = '0' then
                        -- Reference leads feedback
                        if phase_counter < 32767 then
                            phase_counter <= phase_counter + 1;
                        end if;
                    elsif down_ff = '1' and up_ff = '0' then
                        -- Feedback leads reference
                        if phase_counter > -32768 then
                            phase_counter <= phase_counter - 1;
                        end if;
                    end if;

                    -- Capture on reference edge
                    if ref_rising = '1' then
                        error_reg <= to_bitvector_signed(phase_counter, 16);
                    end if;

                when "10" =>
                    -- Frequency detector only
                    if ref_rising = '1' then
                        measuring <= '1';
                        phase_counter <= 0;
                    end if;

                    if measuring = '1' then
                        phase_counter <= phase_counter + 1;
                    end if;

                    if fb_rising = '1' and measuring = '1' then
                        error_reg <= to_bitvector_signed(phase_counter, 16);
                        measuring <= '0';
                    end if;

                when others =>
                    null;
            end case;
        end if;
    end process phase_measure_proc;

    phase_error <= error_reg;

    -- Lock detection
    lock_proc: process(clk, reset)
        variable phase_small : bit;
    begin
        if reset = '1' then
            lock_counter <= 0;
            locked_reg <= '0';
        elsif clk'event and clk = '1' then
            -- Check if phase error is small
            if phase_counter >= -10 and phase_counter <= 10 then
                phase_small := '1';
            else
                phase_small := '0';
            end if;

            if phase_small = '1' then
                if lock_counter < LOCK_THRESHOLD then
                    lock_counter <= lock_counter + 1;
                else
                    locked_reg <= '1';
                end if;
            else
                lock_counter <= 0;
                locked_reg <= '0';
            end if;
        end if;
    end process lock_proc;

    locked <= locked_reg;

    -- Output assignments based on mode
    output_proc: process(mode, up_ff, down_ff, xor_output, ref_synced, fb_synced)
    begin
        case mode is
            when "00" =>
                -- XOR mode
                up <= xor_output and ref_synced;
                down <= xor_output and fb_synced;
                pump_up <= xor_output and ref_synced;
                pump_down <= xor_output and fb_synced;

            when "01" | "11" =>
                -- PFD mode
                up <= up_ff and (not down_ff);
                down <= down_ff and (not up_ff);
                pump_up <= up_ff;
                pump_down <= down_ff;

            when "10" =>
                -- Frequency only
                up <= '0';
                down <= '0';
                pump_up <= '0';
                pump_down <= '0';

            when others =>
                up <= '0';
                down <= '0';
                pump_up <= '0';
                pump_down <= '0';
        end case;
    end process output_proc;

end Behavioral;

-- ============================================================================
-- Bang-Bang Phase Detector (for CDR)
-- ============================================================================
entity BangBangPD is
    port (
        clk         : in  bit;
        reset       : in  bit;
        data_in     : in  bit;  -- Incoming data stream
        sample_clk  : in  bit;  -- Sampling clock
        -- Outputs
        early       : out bit;  -- Data transition before clock
        late        : out bit;  -- Data transition after clock
        data_out    : out bit   -- Recovered data
    );
end BangBangPD;

architecture Behavioral of BangBangPD is
    signal data_sync1, data_sync2, data_sync3 : bit;
    signal sample_sync, sample_prev : bit;
    signal transition : bit;
begin
    -- Sample data at different phases
    sample_proc: process(clk, reset)
    begin
        if reset = '1' then
            data_sync1 <= '0';
            data_sync2 <= '0';
            data_sync3 <= '0';
            sample_sync <= '0';
            sample_prev <= '0';
        elsif clk'event and clk = '1' then
            data_sync1 <= data_in;
            data_sync2 <= data_sync1;
            data_sync3 <= data_sync2;
            sample_sync <= sample_clk;
            sample_prev <= sample_sync;
        end if;
    end process sample_proc;

    -- Detect transition
    transition <= data_sync2 xor data_sync3;

    -- Bang-bang decision
    decision_proc: process(clk, reset)
        variable sample_edge : bit;
    begin
        if reset = '1' then
            early <= '0';
            late <= '0';
            data_out <= '0';
        elsif clk'event and clk = '1' then
            sample_edge := sample_sync and (not sample_prev);

            if sample_edge = '1' then
                data_out <= data_sync2;

                if transition = '1' then
                    -- There was a transition
                    if data_sync1 = data_sync2 then
                        -- Transition already happened - clock is late
                        late <= '1';
                        early <= '0';
                    else
                        -- Transition will happen - clock is early
                        early <= '1';
                        late <= '0';
                    end if;
                else
                    -- No transition - no phase information
                    early <= '0';
                    late <= '0';
                end if;
            end if;
        end if;
    end process decision_proc;

end Behavioral;

-- ============================================================================
-- Digital Phase Accumulator (for DDS/NCO)
-- ============================================================================
entity PhaseAccumulator is
    generic (
        ACC_WIDTH : integer := 32;
        OUTPUT_WIDTH : integer := 12
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        enable          : in  bit;
        frequency_word  : in  bit_vector(ACC_WIDTH-1 downto 0);
        phase_offset    : in  bit_vector(ACC_WIDTH-1 downto 0);
        phase_out       : out bit_vector(OUTPUT_WIDTH-1 downto 0);
        overflow        : out bit  -- Accumulator wrapped
    );
end PhaseAccumulator;

architecture Behavioral of PhaseAccumulator is
    signal accumulator : bit_vector(ACC_WIDTH-1 downto 0);
    signal acc_prev_msb : bit;

    function add_vectors(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable carry : bit := '0';
    begin
        for i in a'low to a'high loop
            result(i) := a(i) xor b(i) xor carry;
            carry := (a(i) and b(i)) or (a(i) and carry) or (b(i) and carry);
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
    begin
        if reset = '1' then
            accumulator <= (others => '0');
            acc_prev_msb <= '0';
            overflow <= '0';
        elsif clk'event and clk = '1' then
            if enable = '1' then
                acc_prev_msb <= accumulator(ACC_WIDTH-1);
                accumulator <= add_vectors(accumulator, frequency_word);

                -- Detect overflow (MSB changed from 1 to 0)
                if acc_prev_msb = '1' and accumulator(ACC_WIDTH-1) = '0' then
                    overflow <= '1';
                else
                    overflow <= '0';
                end if;
            end if;
        end if;
    end process;

    -- Output upper bits with phase offset
    phase_out_proc: process(accumulator, phase_offset)
        variable with_offset : bit_vector(ACC_WIDTH-1 downto 0);
    begin
        with_offset := add_vectors(accumulator, phase_offset);
        phase_out <= with_offset(ACC_WIDTH-1 downto ACC_WIDTH-OUTPUT_WIDTH);
    end process phase_out_proc;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity PhaseDetector_TB is
end PhaseDetector_TB;

architecture Test of PhaseDetector_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_ref_clk, tb_fb_clk : bit;
    signal tb_mode : bit_vector(1 downto 0);
    signal tb_phase_error : bit_vector(15 downto 0);
    signal tb_up, tb_down, tb_locked : bit;
    signal tb_pump_up, tb_pump_down : bit;
begin
    UUT: entity work.PhaseDetector
        port map (
            clk         => tb_clk,
            reset       => tb_reset,
            ref_clk     => tb_ref_clk,
            fb_clk      => tb_fb_clk,
            mode        => tb_mode,
            phase_error => tb_phase_error,
            up          => tb_up,
            down        => tb_down,
            locked      => tb_locked,
            pump_up     => tb_pump_up,
            pump_down   => tb_pump_down
        );

    -- System clock (fast)
    clk_gen: process
    begin
        tb_clk <= '0'; wait for 2 ns;
        tb_clk <= '1'; wait for 2 ns;
    end process;

    -- Reference clock
    ref_clk_gen: process
    begin
        tb_ref_clk <= '0'; wait for 50 ns;
        tb_ref_clk <= '1'; wait for 50 ns;
    end process;

    -- Feedback clock (slightly different frequency for testing)
    fb_clk_gen: process
    begin
        wait for 10 ns;  -- Phase offset
        loop
            tb_fb_clk <= '0'; wait for 48 ns;  -- Slightly faster
            tb_fb_clk <= '1'; wait for 48 ns;
        end loop;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_mode <= "01";  -- PFD mode
        wait for 50 ns;

        tb_reset <= '0';
        wait for 5000 ns;

        -- Try XOR mode
        tb_mode <= "00";
        wait for 5000 ns;

        wait;
    end process;
end Test;
