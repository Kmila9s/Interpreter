-- ============================================================================
-- VHDL Project: GameStateMachine
-- Category: State Machines
-- Complexity: High
-- Description: Game logic controller for arcade-style game
--              Handles player states, scoring, levels, game flow
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity GameStateMachine is
    generic (
        MAX_LIVES    : integer := 3;
        MAX_LEVEL    : integer := 10;
        SCORE_DIGITS : integer := 6
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Player inputs
        start_button    : in  bit;
        action_button   : in  bit;
        joystick_up     : in  bit;
        joystick_down   : in  bit;
        joystick_left   : in  bit;
        joystick_right  : in  bit;
        pause_button    : in  bit;
        -- Game events (from game logic)
        collision       : in  bit;
        enemy_hit       : in  bit;
        powerup_collect : in  bit;
        level_complete  : in  bit;
        boss_defeated   : in  bit;
        -- Outputs
        player_active   : out bit;
        player_invuln   : out bit;
        game_running    : out bit;
        game_paused     : out bit;
        game_over       : out bit;
        show_title      : out bit;
        show_highscore  : out bit;
        -- Game state
        current_lives   : out bit_vector(2 downto 0);
        current_level   : out bit_vector(3 downto 0);
        current_score   : out bit_vector(23 downto 0);
        high_score      : out bit_vector(23 downto 0);
        -- State output
        state_out       : out bit_vector(4 downto 0)
    );
end GameStateMachine;

architecture Behavioral of GameStateMachine is
    -- Main game states
    type game_state_type is (
        POWER_ON,
        TITLE_SCREEN,
        HIGH_SCORE_DISPLAY,
        READY,
        PLAYING,
        PLAYER_HIT,
        RESPAWNING,
        LEVEL_COMPLETE,
        BOSS_FIGHT,
        BOSS_VICTORY,
        LEVEL_TRANSITION,
        GAME_OVER,
        NAME_ENTRY,
        PAUSED
    );

    -- Player sub-states
    type player_state_type is (
        NORMAL,
        POWERED_UP,
        INVULNERABLE,
        DYING
    );

    signal game_state : game_state_type;
    signal player_state : player_state_type;
    signal return_state : game_state_type;

    -- Game variables
    signal lives : integer range 0 to MAX_LIVES;
    signal level : integer range 1 to MAX_LEVEL;
    signal score : integer range 0 to 16777215;  -- 24-bit
    signal hi_score : integer range 0 to 16777215;

    -- Timers
    signal state_timer : integer range 0 to 65535;
    signal invuln_timer : integer range 0 to 1023;
    signal powerup_timer : integer range 0 to 4095;

    -- Flash counter for effects
    signal flash_counter : integer range 0 to 31;
    signal flash_state : bit;

    -- Score multiplier
    signal multiplier : integer range 1 to 8;

    -- Constants
    constant TITLE_TIME : integer := 500;
    constant READY_TIME : integer := 100;
    constant DEATH_TIME : integer := 100;
    constant RESPAWN_TIME : integer := 50;
    constant INVULN_TIME : integer := 200;
    constant LEVEL_TRANS_TIME : integer := 150;
    constant GAME_OVER_TIME : integer := 300;

    constant ENEMY_SCORE : integer := 100;
    constant POWERUP_SCORE : integer := 500;
    constant BOSS_SCORE : integer := 5000;
    constant LEVEL_BONUS : integer := 10000;

    function to_bv(val : integer; width : integer) return bit_vector is
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
    -- Main FSM
    game_fsm: process(clk, reset)
        variable score_add : integer;
    begin
        if reset = '1' then
            game_state <= POWER_ON;
            player_state <= NORMAL;
            lives <= MAX_LIVES;
            level <= 1;
            score <= 0;
            hi_score <= 0;
            state_timer <= 0;
            invuln_timer <= 0;
            powerup_timer <= 0;
            multiplier <= 1;
            flash_counter <= 0;
            flash_state <= '0';
        elsif clk'event and clk = '1' then
            -- Flash counter
            flash_counter <= flash_counter + 1;
            if flash_counter = 0 then
                flash_state <= not flash_state;
            end if;

            -- Invulnerability timer
            if invuln_timer > 0 then
                invuln_timer <= invuln_timer - 1;
                if invuln_timer = 1 then
                    player_state <= NORMAL;
                end if;
            end if;

            -- Powerup timer
            if powerup_timer > 0 then
                powerup_timer <= powerup_timer - 1;
                if powerup_timer = 1 then
                    multiplier <= 1;
                end if;
            end if;

            case game_state is
                when POWER_ON =>
                    state_timer <= state_timer + 1;
                    if state_timer >= 100 then
                        game_state <= TITLE_SCREEN;
                        state_timer <= 0;
                    end if;

                when TITLE_SCREEN =>
                    state_timer <= state_timer + 1;
                    if start_button = '1' then
                        -- Start new game
                        lives <= MAX_LIVES;
                        level <= 1;
                        score <= 0;
                        multiplier <= 1;
                        game_state <= READY;
                        state_timer <= 0;
                    elsif state_timer >= TITLE_TIME then
                        game_state <= HIGH_SCORE_DISPLAY;
                        state_timer <= 0;
                    end if;

                when HIGH_SCORE_DISPLAY =>
                    state_timer <= state_timer + 1;
                    if start_button = '1' then
                        lives <= MAX_LIVES;
                        level <= 1;
                        score <= 0;
                        multiplier <= 1;
                        game_state <= READY;
                        state_timer <= 0;
                    elsif state_timer >= TITLE_TIME then
                        game_state <= TITLE_SCREEN;
                        state_timer <= 0;
                    end if;

                when READY =>
                    player_state <= INVULNERABLE;
                    invuln_timer <= INVULN_TIME;
                    state_timer <= state_timer + 1;
                    if state_timer >= READY_TIME then
                        game_state <= PLAYING;
                        state_timer <= 0;
                    end if;

                when PLAYING =>
                    -- Pause handling
                    if pause_button = '1' then
                        return_state <= PLAYING;
                        game_state <= PAUSED;
                    end if;

                    -- Score events
                    if enemy_hit = '1' then
                        score_add := ENEMY_SCORE * multiplier;
                        if score + score_add < 16777215 then
                            score <= score + score_add;
                        else
                            score <= 16777215;
                        end if;
                    end if;

                    if powerup_collect = '1' then
                        score_add := POWERUP_SCORE * multiplier;
                        if score + score_add < 16777215 then
                            score <= score + score_add;
                        end if;
                        -- Activate powerup
                        powerup_timer <= 1000;
                        if multiplier < 8 then
                            multiplier <= multiplier * 2;
                        end if;
                    end if;

                    -- Collision handling
                    if collision = '1' and player_state = NORMAL then
                        player_state <= DYING;
                        game_state <= PLAYER_HIT;
                        state_timer <= 0;
                    end if;

                    -- Level complete
                    if level_complete = '1' then
                        game_state <= LEVEL_COMPLETE;
                        state_timer <= 0;
                    end if;

                    -- Boss fight trigger (simplified)
                    if level = MAX_LEVEL then
                        game_state <= BOSS_FIGHT;
                    end if;

                when PLAYER_HIT =>
                    state_timer <= state_timer + 1;
                    if state_timer >= DEATH_TIME then
                        if lives > 1 then
                            lives <= lives - 1;
                            game_state <= RESPAWNING;
                        else
                            lives <= 0;
                            game_state <= GAME_OVER;
                        end if;
                        state_timer <= 0;
                    end if;

                when RESPAWNING =>
                    state_timer <= state_timer + 1;
                    if state_timer >= RESPAWN_TIME then
                        player_state <= INVULNERABLE;
                        invuln_timer <= INVULN_TIME;
                        game_state <= PLAYING;
                        state_timer <= 0;
                    end if;

                when LEVEL_COMPLETE =>
                    state_timer <= state_timer + 1;
                    -- Level bonus
                    if state_timer = 1 then
                        score_add := LEVEL_BONUS * level;
                        if score + score_add < 16777215 then
                            score <= score + score_add;
                        else
                            score <= 16777215;
                        end if;
                    end if;

                    if state_timer >= LEVEL_TRANS_TIME then
                        if level < MAX_LEVEL then
                            level <= level + 1;
                            game_state <= LEVEL_TRANSITION;
                        else
                            game_state <= BOSS_FIGHT;
                        end if;
                        state_timer <= 0;
                    end if;

                when BOSS_FIGHT =>
                    if pause_button = '1' then
                        return_state <= BOSS_FIGHT;
                        game_state <= PAUSED;
                    end if;

                    if collision = '1' and player_state = NORMAL then
                        player_state <= DYING;
                        game_state <= PLAYER_HIT;
                        state_timer <= 0;
                    end if;

                    if boss_defeated = '1' then
                        game_state <= BOSS_VICTORY;
                        state_timer <= 0;
                    end if;

                when BOSS_VICTORY =>
                    state_timer <= state_timer + 1;
                    if state_timer = 1 then
                        score_add := BOSS_SCORE * level;
                        if score + score_add < 16777215 then
                            score <= score + score_add;
                        else
                            score <= 16777215;
                        end if;
                    end if;

                    if state_timer >= LEVEL_TRANS_TIME then
                        if level < MAX_LEVEL then
                            level <= level + 1;
                            game_state <= LEVEL_TRANSITION;
                        else
                            -- Game complete!
                            game_state <= GAME_OVER;
                        end if;
                        state_timer <= 0;
                    end if;

                when LEVEL_TRANSITION =>
                    state_timer <= state_timer + 1;
                    if state_timer >= LEVEL_TRANS_TIME then
                        game_state <= READY;
                        state_timer <= 0;
                    end if;

                when GAME_OVER =>
                    state_timer <= state_timer + 1;
                    -- Update high score
                    if score > hi_score then
                        hi_score <= score;
                    end if;

                    if state_timer >= GAME_OVER_TIME then
                        if score > hi_score then
                            game_state <= NAME_ENTRY;
                        else
                            game_state <= TITLE_SCREEN;
                        end if;
                        state_timer <= 0;
                    end if;

                when NAME_ENTRY =>
                    -- Simplified - just wait for button
                    if start_button = '1' or action_button = '1' then
                        game_state <= HIGH_SCORE_DISPLAY;
                        state_timer <= 0;
                    end if;

                when PAUSED =>
                    if pause_button = '1' then
                        game_state <= return_state;
                    end if;
                    if start_button = '1' then
                        -- Quit to title
                        game_state <= TITLE_SCREEN;
                        state_timer <= 0;
                    end if;

            end case;
        end if;
    end process;

    -- Output logic
    player_active <= '1' when game_state = PLAYING or game_state = BOSS_FIGHT else '0';

    player_invuln <= '1' when player_state = INVULNERABLE or player_state = POWERED_UP else '0';

    game_running <= '1' when game_state = PLAYING or game_state = BOSS_FIGHT or
                            game_state = READY or game_state = RESPAWNING else '0';

    game_paused <= '1' when game_state = PAUSED else '0';

    game_over <= '1' when game_state = GAME_OVER else '0';

    show_title <= '1' when game_state = TITLE_SCREEN else '0';

    show_highscore <= '1' when game_state = HIGH_SCORE_DISPLAY or
                              game_state = NAME_ENTRY else '0';

    current_lives <= to_bv(lives, 3);
    current_level <= to_bv(level, 4);
    current_score <= to_bv(score, 24);
    high_score <= to_bv(hi_score, 24);

    -- State encoding
    with game_state select state_out <=
        "00000" when POWER_ON,
        "00001" when TITLE_SCREEN,
        "00010" when HIGH_SCORE_DISPLAY,
        "00011" when READY,
        "00100" when PLAYING,
        "00101" when PLAYER_HIT,
        "00110" when RESPAWNING,
        "00111" when LEVEL_COMPLETE,
        "01000" when BOSS_FIGHT,
        "01001" when BOSS_VICTORY,
        "01010" when LEVEL_TRANSITION,
        "01011" when GAME_OVER,
        "01100" when NAME_ENTRY,
        "01101" when PAUSED;

end Behavioral;

-- ============================================================================
-- Score Display Controller
-- ============================================================================
entity ScoreDisplay is
    port (
        clk         : in  bit;
        reset       : in  bit;
        score       : in  bit_vector(23 downto 0);
        -- BCD outputs for 6 digits
        digit_0     : out bit_vector(3 downto 0);
        digit_1     : out bit_vector(3 downto 0);
        digit_2     : out bit_vector(3 downto 0);
        digit_3     : out bit_vector(3 downto 0);
        digit_4     : out bit_vector(3 downto 0);
        digit_5     : out bit_vector(3 downto 0)
    );
end ScoreDisplay;

architecture Behavioral of ScoreDisplay is
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

    function to_bcd(val : integer) return bit_vector is
        variable result : bit_vector(3 downto 0);
        variable temp : integer := val mod 10;
    begin
        for i in 0 to 3 loop
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
    process(score)
        variable score_int : integer;
    begin
        score_int := to_integer(score);
        digit_0 <= to_bcd(score_int);
        digit_1 <= to_bcd(score_int / 10);
        digit_2 <= to_bcd(score_int / 100);
        digit_3 <= to_bcd(score_int / 1000);
        digit_4 <= to_bcd(score_int / 10000);
        digit_5 <= to_bcd(score_int / 100000);
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity GameStateMachine_TB is
end GameStateMachine_TB;

architecture Test of GameStateMachine_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_start, tb_action, tb_pause : bit;
    signal tb_joy_up, tb_joy_down, tb_joy_left, tb_joy_right : bit;
    signal tb_collision, tb_enemy_hit, tb_powerup : bit;
    signal tb_level_complete, tb_boss_defeated : bit;
    signal tb_player_active, tb_player_invuln : bit;
    signal tb_game_running, tb_game_paused, tb_game_over : bit;
    signal tb_show_title, tb_show_hs : bit;
    signal tb_lives : bit_vector(2 downto 0);
    signal tb_level : bit_vector(3 downto 0);
    signal tb_score, tb_hiscore : bit_vector(23 downto 0);
    signal tb_state : bit_vector(4 downto 0);
begin
    UUT: entity work.GameStateMachine
        generic map (MAX_LIVES => 3, MAX_LEVEL => 10, SCORE_DIGITS => 6)
        port map (
            clk             => tb_clk,
            reset           => tb_reset,
            start_button    => tb_start,
            action_button   => tb_action,
            joystick_up     => tb_joy_up,
            joystick_down   => tb_joy_down,
            joystick_left   => tb_joy_left,
            joystick_right  => tb_joy_right,
            pause_button    => tb_pause,
            collision       => tb_collision,
            enemy_hit       => tb_enemy_hit,
            powerup_collect => tb_powerup,
            level_complete  => tb_level_complete,
            boss_defeated   => tb_boss_defeated,
            player_active   => tb_player_active,
            player_invuln   => tb_player_invuln,
            game_running    => tb_game_running,
            game_paused     => tb_game_paused,
            game_over       => tb_game_over,
            show_title      => tb_show_title,
            show_highscore  => tb_show_hs,
            current_lives   => tb_lives,
            current_level   => tb_level,
            current_score   => tb_score,
            high_score      => tb_hiscore,
            state_out       => tb_state
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_start <= '0'; tb_action <= '0'; tb_pause <= '0';
        tb_joy_up <= '0'; tb_joy_down <= '0';
        tb_joy_left <= '0'; tb_joy_right <= '0';
        tb_collision <= '0'; tb_enemy_hit <= '0';
        tb_powerup <= '0'; tb_level_complete <= '0';
        tb_boss_defeated <= '0';
        wait for 30 ns;

        tb_reset <= '0';

        -- Wait for title screen
        wait for 200 ns;

        -- Press start
        tb_start <= '1'; wait for 20 ns; tb_start <= '0';

        -- Wait for game to start
        wait for 200 ns;

        -- Get some points
        tb_enemy_hit <= '1'; wait for 10 ns; tb_enemy_hit <= '0'; wait for 20 ns;
        tb_enemy_hit <= '1'; wait for 10 ns; tb_enemy_hit <= '0'; wait for 20 ns;

        -- Collect powerup
        tb_powerup <= '1'; wait for 10 ns; tb_powerup <= '0'; wait for 20 ns;

        -- More enemies (with multiplier)
        tb_enemy_hit <= '1'; wait for 10 ns; tb_enemy_hit <= '0'; wait for 20 ns;

        -- Player collision
        tb_collision <= '1'; wait for 10 ns; tb_collision <= '0';

        wait for 500 ns;

        -- Complete level
        tb_level_complete <= '1'; wait for 10 ns; tb_level_complete <= '0';

        wait for 500 ns;
        wait;
    end process;
end Test;
