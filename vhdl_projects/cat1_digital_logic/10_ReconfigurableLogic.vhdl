-- ============================================================================
-- VHDL Project: ReconfigurableLogic
-- Category: Digital Logic Fundamentals
-- Complexity: High
-- Description: Runtime-configurable logic block (mini CLB)
--              Similar to FPGA fabric: LUTs, MUXes, flip-flops, carry chain
--              Can implement any 4-input function with optional registration
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ReconfigurableLogic is
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Configuration
        config_enable   : in  bit;
        config_data     : in  bit_vector(31 downto 0);
        config_load     : in  bit;
        -- Logic inputs
        logic_in        : in  bit_vector(3 downto 0);  -- A, B, C, D
        carry_in        : in  bit;
        -- Logic outputs
        logic_out       : out bit_vector(1 downto 0);  -- Y0, Y1
        carry_out       : out bit;
        -- External connections
        ext_clk         : in  bit;   -- Optional external clock
        ext_reset       : in  bit;   -- Optional external reset
        ext_ce          : in  bit    -- Clock enable
    );
end ReconfigurableLogic;

architecture Behavioral of ReconfigurableLogic is
    -- Configuration register breakdown:
    -- bits 15:0  = LUT0 contents (16 bits for 4-input LUT)
    -- bits 31:16 = LUT1 contents
    signal config_reg : bit_vector(31 downto 0);

    -- Additional configuration (would come from extended config)
    signal use_carry    : bit;       -- Enable carry chain
    signal use_register : bit;       -- Enable output registers
    signal use_ext_clk  : bit;       -- Use external clock
    signal mux_select   : bit;       -- Output MUX select

    -- LUT outputs
    signal lut0_out : bit;
    signal lut1_out : bit;

    -- Carry chain
    signal carry_chain : bit;

    -- Registered outputs
    signal reg_out0, reg_out1 : bit;

    -- Selected clock and reset
    signal selected_clk : bit;
    signal selected_rst : bit;

    -- Function to evaluate 4-input LUT
    function eval_lut4(lut_config : bit_vector(15 downto 0);
                       inputs : bit_vector(3 downto 0)) return bit is
        variable addr : integer range 0 to 15;
    begin
        addr := 0;
        if inputs(0) = '1' then addr := addr + 1; end if;
        if inputs(1) = '1' then addr := addr + 2; end if;
        if inputs(2) = '1' then addr := addr + 4; end if;
        if inputs(3) = '1' then addr := addr + 8; end if;
        return lut_config(addr);
    end function;

begin
    -- Configuration loading
    config_proc: process(clk, reset)
    begin
        if reset = '1' then
            config_reg <= (others => '0');
            use_carry <= '0';
            use_register <= '0';
            use_ext_clk <= '0';
            mux_select <= '0';
        elsif clk'event and clk = '1' then
            if config_enable = '1' and config_load = '1' then
                config_reg <= config_data;
            end if;
        end if;
    end process config_proc;

    -- LUT evaluation
    lut0_out <= eval_lut4(config_reg(15 downto 0), logic_in);
    lut1_out <= eval_lut4(config_reg(31 downto 16), logic_in);

    -- Carry chain logic
    -- Implements: carry_out = (A AND B) OR (carry_in AND (A XOR B))
    -- This is a full adder carry, useful for arithmetic
    carry_proc: process(logic_in, carry_in, use_carry, lut0_out)
        variable p, g : bit;  -- Propagate and generate
    begin
        if use_carry = '1' then
            g := logic_in(0) and logic_in(1);  -- Generate
            p := logic_in(0) xor logic_in(1);  -- Propagate
            carry_chain <= g or (carry_in and p);
        else
            carry_chain <= lut0_out;  -- Use LUT output as carry
        end if;
    end process carry_proc;

    carry_out <= carry_chain;

    -- Clock selection
    selected_clk <= ext_clk when use_ext_clk = '1' else clk;
    selected_rst <= ext_reset when use_ext_clk = '1' else reset;

    -- Output registers
    reg_proc: process(selected_clk, selected_rst)
    begin
        if selected_rst = '1' then
            reg_out0 <= '0';
            reg_out1 <= '0';
        elsif selected_clk'event and selected_clk = '1' then
            if ext_ce = '1' then
                reg_out0 <= lut0_out;
                reg_out1 <= lut1_out;
            end if;
        end if;
    end process reg_proc;

    -- Output multiplexers
    output_mux: process(lut0_out, lut1_out, reg_out0, reg_out1,
                        use_register, carry_chain, mux_select)
    begin
        if use_register = '1' then
            logic_out(0) <= reg_out0;
            logic_out(1) <= reg_out1;
        else
            if mux_select = '0' then
                logic_out(0) <= lut0_out;
                logic_out(1) <= lut1_out;
            else
                logic_out(0) <= carry_chain;
                logic_out(1) <= lut1_out xor carry_chain;  -- Sum output
            end if;
        end if;
    end process output_mux;

end Behavioral;

-- ============================================================================
-- CLB Slice (More complete CLB implementation)
-- ============================================================================
entity CLBSlice is
    port (
        clk         : in  bit;
        reset       : in  bit;
        ce          : in  bit;  -- Clock enable
        -- Inputs
        A, B, C, D  : in  bit;
        CIN         : in  bit;  -- Carry in
        -- Outputs
        O5          : out bit;  -- LUT5 output
        O6          : out bit;  -- LUT6 output
        COUT        : out bit;  -- Carry out
        Q           : out bit;  -- Registered output
        -- Configuration
        LUT_INIT    : in  bit_vector(63 downto 0);  -- LUT6 contents
        FF_INIT     : in  bit;   -- FF initial value
        USE_CARRY   : in  bit;   -- Enable carry logic
        FF_TYPE     : in  bit    -- '0' = FF, '1' = Latch
    );
end CLBSlice;

architecture Behavioral of CLBSlice is
    signal lut5_upper, lut5_lower : bit;
    signal lut6_out : bit;
    signal ff_d, ff_q : bit;
    signal carry_gen, carry_prop : bit;

    function eval_lut(init : bit_vector; addr : integer) return bit is
    begin
        return init(addr);
    end function;

begin
    -- LUT6 = two LUT5s with a mux
    -- LUT5 lower (inputs A,B,C,D, address bits 0-3)
    -- LUT5 upper (inputs A,B,C,D, address bits 0-3 + 32)
    lut_proc: process(A, B, C, D, LUT_INIT)
        variable addr5 : integer range 0 to 31;
    begin
        addr5 := 0;
        if A = '1' then addr5 := addr5 + 1;  end if;
        if B = '1' then addr5 := addr5 + 2;  end if;
        if C = '1' then addr5 := addr5 + 4;  end if;
        if D = '1' then addr5 := addr5 + 8;  end if;

        lut5_lower <= LUT_INIT(addr5);
        lut5_upper <= LUT_INIT(addr5 + 32);
    end process lut_proc;

    -- LUT6 output (fifth input would select between upper/lower)
    -- For simplicity, using D as the select
    lut6_out <= lut5_upper when D = '1' else lut5_lower;
    O5 <= lut5_lower;
    O6 <= lut6_out;

    -- Carry chain
    carry_gen <= A and B;
    carry_prop <= A xor B;

    carry_proc: process(carry_gen, carry_prop, CIN, USE_CARRY, lut6_out)
    begin
        if USE_CARRY = '1' then
            COUT <= carry_gen or (carry_prop and CIN);
        else
            COUT <= lut6_out;
        end if;
    end process carry_proc;

    -- Flip-flop / Latch
    ff_d <= lut6_out;

    ff_proc: process(clk, reset, ce, ff_d, FF_TYPE, FF_INIT)
    begin
        if reset = '1' then
            ff_q <= FF_INIT;
        elsif FF_TYPE = '1' then
            -- Latch mode
            if ce = '1' then
                ff_q <= ff_d;
            end if;
        else
            -- FF mode
            if clk'event and clk = '1' then
                if ce = '1' then
                    ff_q <= ff_d;
                end if;
            end if;
        end if;
    end process ff_proc;

    Q <= ff_q;

end Behavioral;

-- ============================================================================
-- Switch Matrix (for routing between CLBs)
-- ============================================================================
entity SwitchMatrix is
    generic (
        NUM_TRACKS : integer := 4
    );
    port (
        -- Configuration
        config_bits : in  bit_vector(NUM_TRACKS*NUM_TRACKS-1 downto 0);
        -- Input tracks (from adjacent CLBs)
        north_in    : in  bit_vector(NUM_TRACKS-1 downto 0);
        south_in    : in  bit_vector(NUM_TRACKS-1 downto 0);
        east_in     : in  bit_vector(NUM_TRACKS-1 downto 0);
        west_in     : in  bit_vector(NUM_TRACKS-1 downto 0);
        -- Output tracks (to adjacent CLBs)
        north_out   : out bit_vector(NUM_TRACKS-1 downto 0);
        south_out   : out bit_vector(NUM_TRACKS-1 downto 0);
        east_out    : out bit_vector(NUM_TRACKS-1 downto 0);
        west_out    : out bit_vector(NUM_TRACKS-1 downto 0)
    );
end SwitchMatrix;

architecture Behavioral of SwitchMatrix is
begin
    -- Simple switch matrix: each output track can select from any input
    -- Configuration bits control the MUXes
    route_proc: process(config_bits, north_in, south_in, east_in, west_in)
        variable cfg_idx : integer;
    begin
        for i in 0 to NUM_TRACKS-1 loop
            cfg_idx := i * 4;

            -- North output selects from: south_in, east_in, west_in
            case config_bits(cfg_idx+1 downto cfg_idx) is
                when "00" => north_out(i) <= south_in(i);
                when "01" => north_out(i) <= east_in(i);
                when "10" => north_out(i) <= west_in(i);
                when others => north_out(i) <= '0';
            end case;

            -- South output
            case config_bits(cfg_idx+3 downto cfg_idx+2) is
                when "00" => south_out(i) <= north_in(i);
                when "01" => south_out(i) <= east_in(i);
                when "10" => south_out(i) <= west_in(i);
                when others => south_out(i) <= '0';
            end case;

            -- Simplified: East gets from west, West gets from east
            east_out(i) <= west_in(i);
            west_out(i) <= east_in(i);
        end loop;
    end process route_proc;
end Behavioral;

-- ============================================================================
-- Mini FPGA Fabric (2x2 CLB array)
-- ============================================================================
entity MiniFPGA is
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Global inputs
        global_in   : in  bit_vector(7 downto 0);
        -- Global outputs
        global_out  : out bit_vector(7 downto 0);
        -- Configuration interface
        cfg_clk     : in  bit;
        cfg_data    : in  bit;
        cfg_enable  : in  bit
    );
end MiniFPGA;

architecture Structural of MiniFPGA is
    -- CLB outputs
    type clb_out_array is array(0 to 3) of bit_vector(1 downto 0);
    signal clb_outputs : clb_out_array;

    -- Configuration storage (simplified)
    signal config_regs : bit_vector(127 downto 0);
    signal cfg_shift_reg : bit_vector(127 downto 0);

begin
    -- Configuration shift chain
    config_proc: process(cfg_clk, reset)
    begin
        if reset = '1' then
            cfg_shift_reg <= (others => '0');
            config_regs <= (others => '0');
        elsif cfg_clk'event and cfg_clk = '1' then
            if cfg_enable = '1' then
                -- Shift in new config bit
                cfg_shift_reg <= cfg_shift_reg(126 downto 0) & cfg_data;
            else
                -- Latch configuration
                config_regs <= cfg_shift_reg;
            end if;
        end if;
    end process config_proc;

    -- CLB instantiation
    clb_gen: for i in 0 to 3 generate
        clb_inst: entity work.ReconfigurableLogic
            port map (
                clk           => clk,
                reset         => reset,
                config_enable => '1',
                config_data   => config_regs(32*i+31 downto 32*i),
                config_load   => '0',
                logic_in      => global_in(3 downto 0),
                carry_in      => '0',
                logic_out     => clb_outputs(i),
                carry_out     => open,
                ext_clk       => clk,
                ext_reset     => reset,
                ext_ce        => '1'
            );
    end generate clb_gen;

    -- Connect CLB outputs to global outputs
    global_out(1 downto 0) <= clb_outputs(0);
    global_out(3 downto 2) <= clb_outputs(1);
    global_out(5 downto 4) <= clb_outputs(2);
    global_out(7 downto 6) <= clb_outputs(3);

end Structural;

-- ============================================================================
-- Testbench
-- ============================================================================
entity ReconfigurableLogic_TB is
end ReconfigurableLogic_TB;

architecture Test of ReconfigurableLogic_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_config_enable, tb_config_load : bit;
    signal tb_config_data : bit_vector(31 downto 0);
    signal tb_logic_in : bit_vector(3 downto 0);
    signal tb_carry_in : bit;
    signal tb_logic_out : bit_vector(1 downto 0);
    signal tb_carry_out : bit;
begin
    UUT: entity work.ReconfigurableLogic
        port map (
            clk           => tb_clk,
            reset         => tb_reset,
            config_enable => tb_config_enable,
            config_data   => tb_config_data,
            config_load   => tb_config_load,
            logic_in      => tb_logic_in,
            carry_in      => tb_carry_in,
            logic_out     => tb_logic_out,
            carry_out     => tb_carry_out,
            ext_clk       => tb_clk,
            ext_reset     => tb_reset,
            ext_ce        => '1'
        );

    -- Clock
    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_config_enable <= '0';
        tb_config_load <= '0';
        tb_config_data <= (others => '0');
        tb_logic_in <= "0000";
        tb_carry_in <= '0';
        wait for 20 ns;

        tb_reset <= '0';

        -- Configure LUT0 as AND gate (output 1 only when all inputs are 1)
        -- LUT truth table: only address 15 (1111) = 1
        tb_config_enable <= '1';
        tb_config_data <= x"00008000";  -- Bit 15 set in LUT0
        tb_config_load <= '1';
        wait for 10 ns;
        tb_config_load <= '0';
        tb_config_enable <= '0';

        -- Test AND function
        tb_logic_in <= "0000"; wait for 10 ns;
        tb_logic_in <= "0001"; wait for 10 ns;
        tb_logic_in <= "0111"; wait for 10 ns;
        tb_logic_in <= "1111"; wait for 10 ns;  -- Should output 1

        -- Configure LUT0 as OR gate
        tb_config_enable <= '1';
        tb_config_data <= x"0000FFFE";  -- All bits except 0 set
        tb_config_load <= '1';
        wait for 10 ns;
        tb_config_load <= '0';
        tb_config_enable <= '0';

        -- Test OR function
        tb_logic_in <= "0000"; wait for 10 ns;  -- Should output 0
        tb_logic_in <= "0001"; wait for 10 ns;  -- Should output 1
        tb_logic_in <= "1000"; wait for 10 ns;  -- Should output 1

        wait;
    end process;
end Test;
