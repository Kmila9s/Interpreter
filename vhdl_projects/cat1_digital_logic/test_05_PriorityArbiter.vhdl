-- ============================================================================
-- VHDL Project: PriorityArbiter
-- Category: Digital Logic Fundamentals
-- Complexity: Medium
-- Description: 8-input priority arbiter with request/grant protocol
--              Fixed priority (input 0 highest) and programmable priority
--              Valid output indicates active request
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity PriorityArbiter is
    port (
        requests    : in  bit_vector(7 downto 0);
        grants      : out bit_vector(7 downto 0);
        grant_index : out bit_vector(2 downto 0);
        valid       : out bit;
        any_request : out bit
    );
end PriorityArbiter;

architecture Behavioral of PriorityArbiter is
    signal priority_mask : bit_vector(7 downto 0);
begin
    -- Check if any request is active
    any_request <= requests(0) or requests(1) or requests(2) or requests(3) or
                   requests(4) or requests(5) or requests(6) or requests(7);

    -- Priority encoding with grant generation
    -- Input 0 has highest priority, Input 7 has lowest
    priority_process: process(requests)
    begin
        -- Default: no grant
        grants <= (others => '0');
        grant_index <= "000";
        valid <= '0';

        -- Check from highest priority to lowest
        if requests(0) = '1' then
            grants(0) <= '1';
            grant_index <= "000";
            valid <= '1';
        elsif requests(1) = '1' then
            grants(1) <= '1';
            grant_index <= "001";
            valid <= '1';
        elsif requests(2) = '1' then
            grants(2) <= '1';
            grant_index <= "010";
            valid <= '1';
        elsif requests(3) = '1' then
            grants(3) <= '1';
            grant_index <= "011";
            valid <= '1';
        elsif requests(4) = '1' then
            grants(4) <= '1';
            grant_index <= "100";
            valid <= '1';
        elsif requests(5) = '1' then
            grants(5) <= '1';
            grant_index <= "101";
            valid <= '1';
        elsif requests(6) = '1' then
            grants(6) <= '1';
            grant_index <= "110";
            valid <= '1';
        elsif requests(7) = '1' then
            grants(7) <= '1';
            grant_index <= "111";
            valid <= '1';
        end if;
    end process priority_process;
end Behavioral;

-- ============================================================================
-- Programmable Priority Arbiter
-- ============================================================================
entity ProgrammablePriorityArbiter is
    port (
        requests      : in  bit_vector(7 downto 0);
        priority_sel  : in  bit_vector(2 downto 0);  -- Highest priority input
        grants        : out bit_vector(7 downto 0);
        grant_index   : out bit_vector(2 downto 0);
        valid         : out bit
    );
end ProgrammablePriorityArbiter;

architecture Behavioral of ProgrammablePriorityArbiter is
    -- Rotated requests based on priority selection
    signal rotated_req : bit_vector(7 downto 0);
    signal rotated_grant : bit_vector(7 downto 0);
    signal internal_index : bit_vector(2 downto 0);
    signal internal_valid : bit;

    -- Function to rotate vector
    function rotate_right(vec : bit_vector(7 downto 0); amount : integer)
        return bit_vector is
        variable result : bit_vector(7 downto 0);
        variable idx : integer;
    begin
        for i in 0 to 7 loop
            idx := (i + amount) mod 8;
            result(i) := vec(idx);
        end loop;
        return result;
    end function;

    -- Function to convert bit_vector to integer
    function to_int(vec : bit_vector(2 downto 0)) return integer is
        variable result : integer := 0;
    begin
        if vec(2) = '1' then result := result + 4; end if;
        if vec(1) = '1' then result := result + 2; end if;
        if vec(0) = '1' then result := result + 1; end if;
        return result;
    end function;

begin
    -- Rotate requests so that priority_sel input becomes position 0
    rotate_process: process(requests, priority_sel)
        variable rot_amount : integer;
    begin
        rot_amount := to_int(priority_sel);
        rotated_req <= rotate_right(requests, rot_amount);
    end process rotate_process;

    -- Use basic priority arbiter on rotated requests
    base_arbiter: entity work.PriorityArbiter
        port map (
            requests    => rotated_req,
            grants      => rotated_grant,
            grant_index => internal_index,
            valid       => internal_valid,
            any_request => open
        );

    -- Rotate grants back
    output_process: process(rotated_grant, internal_index, internal_valid, priority_sel)
        variable rot_amount : integer;
        variable final_idx : integer;
    begin
        rot_amount := to_int(priority_sel);

        -- Rotate grants back
        grants <= rotate_right(rotated_grant, 8 - rot_amount);

        -- Adjust index
        final_idx := (to_int(internal_index) + rot_amount) mod 8;
        grant_index(2) <= '1' when final_idx >= 4 else '0';
        grant_index(1) <= '1' when (final_idx mod 4) >= 2 else '0';
        grant_index(0) <= '1' when (final_idx mod 2) = 1 else '0';

        valid <= internal_valid;
    end process output_process;
end Behavioral;

-- ============================================================================
-- Round-Robin Arbiter (Fair arbitration)
-- ============================================================================
entity RoundRobinArbiter is
    port (
        clk         : in  bit;
        reset       : in  bit;
        requests    : in  bit_vector(7 downto 0);
        grants      : out bit_vector(7 downto 0);
        grant_index : out bit_vector(2 downto 0);
        valid       : out bit
    );
end RoundRobinArbiter;

architecture Behavioral of RoundRobinArbiter is
    signal last_grant : bit_vector(2 downto 0);
    signal next_priority : bit_vector(2 downto 0);
begin
    -- Round-robin: after granting to input N, input N+1 gets highest priority
    arbiter_process: process(clk, reset)
        variable found : bit;
        variable check_idx : integer;
        variable start_idx : integer;
    begin
        if reset = '1' then
            last_grant <= "000";
            grants <= (others => '0');
            grant_index <= "000";
            valid <= '0';
        elsif clk'event and clk = '1' then
            found := '0';
            grants <= (others => '0');
            valid <= '0';

            -- Start checking from position after last grant
            if last_grant = "111" then
                start_idx := 0;
            else
                start_idx := 1;
                if last_grant(0) = '1' then start_idx := start_idx + 1; end if;
                if last_grant(1) = '1' then start_idx := start_idx + 2; end if;
                if last_grant(2) = '1' then start_idx := start_idx + 4; end if;
            end if;

            -- Check all 8 inputs starting from start_idx
            for i in 0 to 7 loop
                check_idx := (start_idx + i) mod 8;
                if found = '0' then
                    case check_idx is
                        when 0 =>
                            if requests(0) = '1' then
                                grants(0) <= '1';
                                grant_index <= "000";
                                last_grant <= "000";
                                valid <= '1';
                                found := '1';
                            end if;
                        when 1 =>
                            if requests(1) = '1' then
                                grants(1) <= '1';
                                grant_index <= "001";
                                last_grant <= "001";
                                valid <= '1';
                                found := '1';
                            end if;
                        when 2 =>
                            if requests(2) = '1' then
                                grants(2) <= '1';
                                grant_index <= "010";
                                last_grant <= "010";
                                valid <= '1';
                                found := '1';
                            end if;
                        when 3 =>
                            if requests(3) = '1' then
                                grants(3) <= '1';
                                grant_index <= "011";
                                last_grant <= "011";
                                valid <= '1';
                                found := '1';
                            end if;
                        when 4 =>
                            if requests(4) = '1' then
                                grants(4) <= '1';
                                grant_index <= "100";
                                last_grant <= "100";
                                valid <= '1';
                                found := '1';
                            end if;
                        when 5 =>
                            if requests(5) = '1' then
                                grants(5) <= '1';
                                grant_index <= "101";
                                last_grant <= "101";
                                valid <= '1';
                                found := '1';
                            end if;
                        when 6 =>
                            if requests(6) = '1' then
                                grants(6) <= '1';
                                grant_index <= "110";
                                last_grant <= "110";
                                valid <= '1';
                                found := '1';
                            end if;
                        when 7 =>
                            if requests(7) = '1' then
                                grants(7) <= '1';
                                grant_index <= "111";
                                last_grant <= "111";
                                valid <= '1';
                                found := '1';
                            end if;
                        when others => null;
                    end case;
                end if;
            end loop;
        end if;
    end process arbiter_process;
end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity PriorityArbiter_TB is
end PriorityArbiter_TB;

architecture Test of PriorityArbiter_TB is
    signal tb_requests    : bit_vector(7 downto 0);
    signal tb_grants      : bit_vector(7 downto 0);
    signal tb_grant_index : bit_vector(2 downto 0);
    signal tb_valid       : bit;
    signal tb_any_request : bit;
begin
    UUT: entity work.PriorityArbiter
        port map (
            requests    => tb_requests,
            grants      => tb_grants,
            grant_index => tb_grant_index,
            valid       => tb_valid,
            any_request => tb_any_request
        );

    stimulus: process
    begin
        -- No requests
        tb_requests <= "00000000"; wait for 10 ns;

        -- Single request at different positions
        tb_requests <= "00000001"; wait for 10 ns;  -- Request 0
        tb_requests <= "00001000"; wait for 10 ns;  -- Request 3
        tb_requests <= "10000000"; wait for 10 ns;  -- Request 7

        -- Multiple requests (should grant highest priority)
        tb_requests <= "11111111"; wait for 10 ns;  -- All requests, grant 0
        tb_requests <= "11111110"; wait for 10 ns;  -- Grant 1
        tb_requests <= "11110000"; wait for 10 ns;  -- Grant 4
        tb_requests <= "10100000"; wait for 10 ns;  -- Grant 5

        wait;
    end process stimulus;
end Test;
