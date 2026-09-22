-- ============================================================================
-- VHDL Project: LookupTableGen
-- Category: Digital Logic Fundamentals
-- Complexity: High
-- Description: Configurable LUT-based function generator
--              Supports arbitrary truth tables, cascading, and
--              runtime configuration for any combinational function
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity LookupTableGen is
    generic (
        INPUT_WIDTH : integer := 4;   -- Number of input bits (up to 6)
        OUTPUT_WIDTH : integer := 2    -- Number of output bits
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- LUT programming interface
        program_enable  : in  bit;
        program_addr    : in  bit_vector(INPUT_WIDTH-1 downto 0);
        program_data    : in  bit_vector(OUTPUT_WIDTH-1 downto 0);
        program_we      : in  bit;
        -- Function evaluation
        func_input      : in  bit_vector(INPUT_WIDTH-1 downto 0);
        func_output     : out bit_vector(OUTPUT_WIDTH-1 downto 0);
        -- Optional registered output
        reg_enable      : in  bit;
        reg_output      : out bit_vector(OUTPUT_WIDTH-1 downto 0)
    );
end LookupTableGen;

architecture Behavioral of LookupTableGen is
    -- Calculate LUT size: 2^INPUT_WIDTH entries
    constant LUT_SIZE : integer := 2**INPUT_WIDTH;

    -- LUT storage type
    type lut_array_type is array (0 to LUT_SIZE-1) of bit_vector(OUTPUT_WIDTH-1 downto 0);
    signal lut_mem : lut_array_type;

    -- Internal signals
    signal lut_output : bit_vector(OUTPUT_WIDTH-1 downto 0);
    signal reg_out_internal : bit_vector(OUTPUT_WIDTH-1 downto 0);

    -- Function to convert bit_vector to integer
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
    -- LUT programming process
    program_proc: process(clk, reset)
    begin
        if reset = '1' then
            -- Initialize LUT to all zeros
            for i in 0 to LUT_SIZE-1 loop
                lut_mem(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            if program_enable = '1' and program_we = '1' then
                lut_mem(to_integer(program_addr)) <= program_data;
            end if;
        end if;
    end process program_proc;

    -- Combinational LUT evaluation
    lut_output <= lut_mem(to_integer(func_input));

    -- Direct output
    func_output <= lut_output;

    -- Registered output
    reg_proc: process(clk, reset)
    begin
        if reset = '1' then
            reg_out_internal <= (others => '0');
        elsif clk'event and clk = '1' then
            if reg_enable = '1' then
                reg_out_internal <= lut_output;
            end if;
        end if;
    end process reg_proc;

    reg_output <= reg_out_internal;

end Behavioral;

-- ============================================================================
-- 6-input LUT (matches Xilinx LUT6)
-- ============================================================================
entity LUT6 is
    port (
        I0, I1, I2, I3, I4, I5 : in  bit;
        O                       : out bit;
        -- Initialization (64 bits for 2^6 entries)
        INIT                    : in  bit_vector(63 downto 0)
    );
end LUT6;

architecture Behavioral of LUT6 is
begin
    process(I0, I1, I2, I3, I4, I5, INIT)
        variable addr : integer range 0 to 63;
    begin
        addr := 0;
        if I0 = '1' then addr := addr + 1;  end if;
        if I1 = '1' then addr := addr + 2;  end if;
        if I2 = '1' then addr := addr + 4;  end if;
        if I3 = '1' then addr := addr + 8;  end if;
        if I4 = '1' then addr := addr + 16; end if;
        if I5 = '1' then addr := addr + 32; end if;
        O <= INIT(addr);
    end process;
end Behavioral;

-- ============================================================================
-- Cascaded LUT for larger functions
-- ============================================================================
entity CascadedLUT is
    generic (
        TOTAL_INPUTS : integer := 8   -- Total input bits (>6 uses cascading)
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        inputs      : in  bit_vector(TOTAL_INPUTS-1 downto 0);
        output      : out bit;
        -- Programming interface
        prog_enable : in  bit;
        prog_addr   : in  bit_vector(TOTAL_INPUTS-1 downto 0);
        prog_data   : in  bit;
        prog_we     : in  bit
    );
end CascadedLUT;

architecture Behavioral of CascadedLUT is
    -- For 8 inputs, we need 2^8 = 256 bits of storage
    -- Use 4x LUT6 + 1 MUX LUT
    constant MEM_SIZE : integer := 2**TOTAL_INPUTS;
    type mem_type is array(0 to MEM_SIZE-1) of bit;
    signal lut_mem : mem_type;

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
    -- Programming
    prog_proc: process(clk, reset)
    begin
        if reset = '1' then
            for i in 0 to MEM_SIZE-1 loop
                lut_mem(i) <= '0';
            end loop;
        elsif clk'event and clk = '1' then
            if prog_enable = '1' and prog_we = '1' then
                lut_mem(to_integer(prog_addr)) <= prog_data;
            end if;
        end if;
    end process prog_proc;

    -- Evaluation
    output <= lut_mem(to_integer(inputs));

end Behavioral;

-- ============================================================================
-- Multi-output Function Generator
-- ============================================================================
entity MultiFunctionGen is
    generic (
        NUM_INPUTS  : integer := 4;
        NUM_OUTPUTS : integer := 4
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Inputs
        x           : in  bit_vector(NUM_INPUTS-1 downto 0);
        -- Outputs
        y           : out bit_vector(NUM_OUTPUTS-1 downto 0);
        -- Pre-defined function select (no programming needed)
        func_select : in  bit_vector(3 downto 0)
        -- "0000" = Identity (lower bits)
        -- "0001" = NOT
        -- "0010" = AND reduction
        -- "0011" = OR reduction
        -- "0100" = XOR reduction (parity)
        -- "0101" = Population count
        -- "0110" = Thermometer code
        -- "0111" = Priority encode
        -- "1000" = Gray encode
        -- "1001" = Reverse bits
    );
end MultiFunctionGen;

architecture Behavioral of MultiFunctionGen is
    signal identity_out : bit_vector(NUM_OUTPUTS-1 downto 0);
    signal not_out : bit_vector(NUM_OUTPUTS-1 downto 0);
    signal and_result : bit;
    signal or_result : bit;
    signal xor_result : bit;
    signal popcount : integer range 0 to NUM_INPUTS;
    signal popcount_bv : bit_vector(NUM_OUTPUTS-1 downto 0);
begin
    -- Identity
    identity_gen: for i in 0 to NUM_OUTPUTS-1 generate
        identity_out(i) <= x(i) when i < NUM_INPUTS else '0';
    end generate;

    -- NOT
    not_gen: for i in 0 to NUM_OUTPUTS-1 generate
        not_out(i) <= not x(i) when i < NUM_INPUTS else '1';
    end generate;

    -- Reductions
    reduce_proc: process(x)
        variable and_temp, or_temp, xor_temp : bit;
        variable pop_temp : integer;
    begin
        and_temp := '1';
        or_temp := '0';
        xor_temp := '0';
        pop_temp := 0;

        for i in 0 to NUM_INPUTS-1 loop
            and_temp := and_temp and x(i);
            or_temp := or_temp or x(i);
            xor_temp := xor_temp xor x(i);
            if x(i) = '1' then
                pop_temp := pop_temp + 1;
            end if;
        end loop;

        and_result <= and_temp;
        or_result <= or_temp;
        xor_result <= xor_temp;
        popcount <= pop_temp;
    end process;

    -- Convert popcount to bit vector
    popcount_conv: process(popcount)
        variable temp : integer;
    begin
        temp := popcount;
        for i in 0 to NUM_OUTPUTS-1 loop
            if (temp mod 2) = 1 then
                popcount_bv(i) <= '1';
            else
                popcount_bv(i) <= '0';
            end if;
            temp := temp / 2;
        end loop;
    end process;

    -- Output multiplexer
    output_mux: process(func_select, identity_out, not_out, and_result,
                        or_result, xor_result, popcount_bv, x)
        variable gray_temp : bit_vector(NUM_OUTPUTS-1 downto 0);
        variable reverse_temp : bit_vector(NUM_OUTPUTS-1 downto 0);
    begin
        -- Calculate Gray code
        if NUM_INPUTS > 0 then
            gray_temp(NUM_INPUTS-1) := x(NUM_INPUTS-1);
            for i in 0 to NUM_INPUTS-2 loop
                gray_temp(i) := x(i) xor x(i+1);
            end loop;
            for i in NUM_INPUTS to NUM_OUTPUTS-1 loop
                gray_temp(i) := '0';
            end loop;
        end if;

        -- Calculate reverse
        for i in 0 to NUM_INPUTS-1 loop
            reverse_temp(i) := x(NUM_INPUTS-1-i);
        end loop;
        for i in NUM_INPUTS to NUM_OUTPUTS-1 loop
            reverse_temp(i) := '0';
        end loop;

        case func_select is
            when "0000" => y <= identity_out;
            when "0001" => y <= not_out;
            when "0010" =>
                y <= (others => '0');
                y(0) <= and_result;
            when "0011" =>
                y <= (others => '0');
                y(0) <= or_result;
            when "0100" =>
                y <= (others => '0');
                y(0) <= xor_result;
            when "0101" => y <= popcount_bv;
            when "1000" => y <= gray_temp;
            when "1001" => y <= reverse_temp;
            when others => y <= identity_out;
        end case;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity LookupTableGen_TB is
end LookupTableGen_TB;

architecture Test of LookupTableGen_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_prog_enable, tb_prog_we, tb_reg_enable : bit;
    signal tb_prog_addr, tb_func_input : bit_vector(3 downto 0);
    signal tb_prog_data, tb_func_output, tb_reg_output : bit_vector(1 downto 0);
begin
    UUT: entity work.LookupTableGen
        generic map (
            INPUT_WIDTH => 4,
            OUTPUT_WIDTH => 2
        )
        port map (
            clk            => tb_clk,
            reset          => tb_reset,
            program_enable => tb_prog_enable,
            program_addr   => tb_prog_addr,
            program_data   => tb_prog_data,
            program_we     => tb_prog_we,
            func_input     => tb_func_input,
            func_output    => tb_func_output,
            reg_enable     => tb_reg_enable,
            reg_output     => tb_reg_output
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
        tb_prog_enable <= '0';
        tb_prog_we <= '0';
        tb_reg_enable <= '1';
        tb_prog_addr <= "0000";
        tb_prog_data <= "00";
        tb_func_input <= "0000";
        wait for 20 ns;

        tb_reset <= '0';

        -- Program some entries (simple incrementer mod 4)
        tb_prog_enable <= '1';
        tb_prog_we <= '1';

        tb_prog_addr <= "0000"; tb_prog_data <= "01"; wait for 10 ns;
        tb_prog_addr <= "0001"; tb_prog_data <= "10"; wait for 10 ns;
        tb_prog_addr <= "0010"; tb_prog_data <= "11"; wait for 10 ns;
        tb_prog_addr <= "0011"; tb_prog_data <= "00"; wait for 10 ns;

        tb_prog_enable <= '0';
        tb_prog_we <= '0';

        -- Test evaluation
        tb_func_input <= "0000"; wait for 10 ns;
        tb_func_input <= "0001"; wait for 10 ns;
        tb_func_input <= "0010"; wait for 10 ns;
        tb_func_input <= "0011"; wait for 10 ns;

        wait;
    end process;
end Test;
