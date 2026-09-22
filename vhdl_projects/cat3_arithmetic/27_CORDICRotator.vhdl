-- ============================================================================
-- VHDL Project: CORDICRotator
-- Category: Arithmetic & Mathematics
-- Complexity: High
-- Description: CORDIC algorithm for sin/cos/atan calculation
--              Iterative coordinate rotation for trigonometric functions
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity CORDICRotator is
    generic (
        WIDTH      : integer := 16;
        ITERATIONS : integer := 14
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        start       : in  bit;
        mode        : in  bit_vector(1 downto 0);
        -- "00" = Rotation: compute sin/cos of angle
        -- "01" = Vectoring: compute atan and magnitude
        -- "10" = Hyperbolic rotation
        -- "11" = Reserved
        x_in        : in  bit_vector(WIDTH-1 downto 0);
        y_in        : in  bit_vector(WIDTH-1 downto 0);
        z_in        : in  bit_vector(WIDTH-1 downto 0);  -- Angle input
        x_out       : out bit_vector(WIDTH-1 downto 0);
        y_out       : out bit_vector(WIDTH-1 downto 0);
        z_out       : out bit_vector(WIDTH-1 downto 0);
        done        : out bit
    );
end CORDICRotator;

architecture Behavioral of CORDICRotator is
    type state_type is (IDLE, ITERATING, OUTPUT);
    signal state : state_type;

    -- Working registers
    signal x_reg : bit_vector(WIDTH-1 downto 0);
    signal y_reg : bit_vector(WIDTH-1 downto 0);
    signal z_reg : bit_vector(WIDTH-1 downto 0);

    signal iteration : integer range 0 to ITERATIONS;

    -- CORDIC angles (atan(2^-i) in fixed point)
    -- For 16-bit with 14 fractional bits
    type angle_rom_type is array (0 to ITERATIONS-1) of bit_vector(WIDTH-1 downto 0);

    -- Pre-computed atan values (scaled to fit WIDTH bits)
    -- atan(1) = 45 degrees = 0.785398... radians
    -- Scaled: 0.785398 * 2^14 = 12867.96 ≈ 12868
    constant ANGLE_ROM : angle_rom_type := (
        "0011001001000100",  -- atan(2^0)  = 45.0 deg
        "0001110110101100",  -- atan(2^-1) = 26.565 deg
        "0000111110101110",  -- atan(2^-2) = 14.036 deg
        "0000011111110101",  -- atan(2^-3) = 7.125 deg
        "0000001111111110",  -- atan(2^-4) = 3.576 deg
        "0000000111111111",  -- atan(2^-5) = 1.790 deg
        "0000000011111111",  -- atan(2^-6) = 0.895 deg
        "0000000001111111",  -- atan(2^-7) = 0.448 deg
        "0000000000111111",  -- atan(2^-8) = 0.224 deg
        "0000000000011111",  -- atan(2^-9) = 0.112 deg
        "0000000000001111",  -- atan(2^-10)
        "0000000000000111",  -- atan(2^-11)
        "0000000000000011",  -- atan(2^-12)
        "0000000000000001"   -- atan(2^-13)
    );

    -- Arithmetic functions
    function shift_right_arith(val : bit_vector; amount : integer) return bit_vector is
        variable result : bit_vector(val'range);
        variable sign : bit;
    begin
        sign := val(val'high);
        result := val;
        for i in 1 to amount loop
            result := sign & result(result'high downto 1);
        end loop;
        return result;
    end function;

    function add(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable carry : bit := '0';
    begin
        for i in a'low to a'high loop
            result(i) := a(i) xor b(i) xor carry;
            carry := (a(i) and b(i)) or (a(i) and carry) or (b(i) and carry);
        end loop;
        return result;
    end function;

    function subtract(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable borrow : bit := '0';
    begin
        for i in a'low to a'high loop
            result(i) := a(i) xor b(i) xor borrow;
            borrow := (not a(i) and b(i)) or (not a(i) and borrow) or (b(i) and borrow);
        end loop;
        return result;
    end function;

begin
    cordic_proc: process(clk, reset)
        variable x_shifted : bit_vector(WIDTH-1 downto 0);
        variable y_shifted : bit_vector(WIDTH-1 downto 0);
        variable sigma : bit;  -- Direction of rotation
    begin
        if reset = '1' then
            state <= IDLE;
            done <= '0';
            x_reg <= (others => '0');
            y_reg <= (others => '0');
            z_reg <= (others => '0');
            iteration <= 0;
        elsif clk'event and clk = '1' then
            case state is
                when IDLE =>
                    done <= '0';
                    if start = '1' then
                        x_reg <= x_in;
                        y_reg <= y_in;
                        z_reg <= z_in;
                        iteration <= 0;
                        state <= ITERATING;
                    end if;

                when ITERATING =>
                    -- Compute shifted values
                    x_shifted := shift_right_arith(x_reg, iteration);
                    y_shifted := shift_right_arith(y_reg, iteration);

                    -- Determine rotation direction based on mode
                    case mode is
                        when "00" =>
                            -- Rotation mode: rotate toward z = 0
                            sigma := z_reg(WIDTH-1);  -- Sign of z
                        when "01" =>
                            -- Vectoring mode: rotate toward y = 0
                            sigma := not y_reg(WIDTH-1);  -- Opposite sign of y
                        when others =>
                            sigma := z_reg(WIDTH-1);
                    end case;

                    -- CORDIC iteration
                    if sigma = '1' then
                        -- Rotate clockwise (negative direction)
                        x_reg <= add(x_reg, y_shifted);
                        y_reg <= subtract(y_reg, x_shifted);
                        z_reg <= add(z_reg, ANGLE_ROM(iteration));
                    else
                        -- Rotate counter-clockwise (positive direction)
                        x_reg <= subtract(x_reg, y_shifted);
                        y_reg <= add(y_reg, x_shifted);
                        z_reg <= subtract(z_reg, ANGLE_ROM(iteration));
                    end if;

                    iteration <= iteration + 1;
                    if iteration >= ITERATIONS - 1 then
                        state <= OUTPUT;
                    end if;

                when OUTPUT =>
                    x_out <= x_reg;
                    y_out <= y_reg;
                    z_out <= z_reg;
                    done <= '1';
                    state <= IDLE;

            end case;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Sin/Cos Generator using CORDIC
-- ============================================================================
entity SinCosGenerator is
    generic (
        WIDTH : integer := 16
    );
    port (
        clk     : in  bit;
        reset   : in  bit;
        start   : in  bit;
        angle   : in  bit_vector(WIDTH-1 downto 0);  -- Input angle
        sin_out : out bit_vector(WIDTH-1 downto 0);
        cos_out : out bit_vector(WIDTH-1 downto 0);
        done    : out bit
    );
end SinCosGenerator;

architecture Behavioral of SinCosGenerator is
    signal cordic_x_in, cordic_y_in : bit_vector(WIDTH-1 downto 0);
    signal cordic_x_out, cordic_y_out, cordic_z_out : bit_vector(WIDTH-1 downto 0);
    signal cordic_done : bit;
    signal started : bit;

    -- CORDIC gain factor K = 0.6073 (need to multiply input by 1/K = 1.6468)
    -- For simplicity, we use x_in = K * max_value ≈ 0x4DBA for 16-bit
    constant CORDIC_K : bit_vector(WIDTH-1 downto 0) := "0100110110111010";

begin
    -- Initialize x with CORDIC constant, y = 0, z = angle
    cordic_x_in <= CORDIC_K;
    cordic_y_in <= (others => '0');

    cordic_inst: entity work.CORDICRotator
        generic map (WIDTH => WIDTH, ITERATIONS => 14)
        port map (
            clk   => clk,
            reset => reset,
            start => start,
            mode  => "00",  -- Rotation mode
            x_in  => cordic_x_in,
            y_in  => cordic_y_in,
            z_in  => angle,
            x_out => cordic_x_out,
            y_out => cordic_y_out,
            z_out => cordic_z_out,
            done  => cordic_done
        );

    -- In rotation mode:
    -- x_out = K * cos(angle)
    -- y_out = K * sin(angle)
    cos_out <= cordic_x_out;
    sin_out <= cordic_y_out;
    done <= cordic_done;

end Behavioral;

-- ============================================================================
-- Atan2 Calculator using CORDIC
-- ============================================================================
entity Atan2Calculator is
    generic (
        WIDTH : integer := 16
    );
    port (
        clk     : in  bit;
        reset   : in  bit;
        start   : in  bit;
        x_in    : in  bit_vector(WIDTH-1 downto 0);
        y_in    : in  bit_vector(WIDTH-1 downto 0);
        angle   : out bit_vector(WIDTH-1 downto 0);  -- atan2(y, x)
        magnitude : out bit_vector(WIDTH-1 downto 0);  -- sqrt(x^2 + y^2) * K
        done    : out bit
    );
end Atan2Calculator;

architecture Behavioral of Atan2Calculator is
    signal cordic_x_out, cordic_y_out, cordic_z_out : bit_vector(WIDTH-1 downto 0);
    signal cordic_done : bit;
begin
    cordic_inst: entity work.CORDICRotator
        generic map (WIDTH => WIDTH, ITERATIONS => 14)
        port map (
            clk   => clk,
            reset => reset,
            start => start,
            mode  => "01",  -- Vectoring mode
            x_in  => x_in,
            y_in  => y_in,
            z_in  => (others => '0'),
            x_out => cordic_x_out,
            y_out => cordic_y_out,
            z_out => cordic_z_out,
            done  => cordic_done
        );

    -- In vectoring mode:
    -- x_out = K * sqrt(x^2 + y^2)
    -- z_out = atan2(y, x)
    magnitude <= cordic_x_out;
    angle <= cordic_z_out;
    done <= cordic_done;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity CORDICRotator_TB is
end CORDICRotator_TB;

architecture Test of CORDICRotator_TB is
    signal tb_clk, tb_reset, tb_start : bit;
    signal tb_angle : bit_vector(15 downto 0);
    signal tb_sin, tb_cos : bit_vector(15 downto 0);
    signal tb_done : bit;
begin
    UUT: entity work.SinCosGenerator
        generic map (WIDTH => 16)
        port map (
            clk     => tb_clk,
            reset   => tb_reset,
            start   => tb_start,
            angle   => tb_angle,
            sin_out => tb_sin,
            cos_out => tb_cos,
            done    => tb_done
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_start <= '0';
        tb_angle <= (others => '0');
        wait for 30 ns;

        tb_reset <= '0';

        -- Test angle = 0 (should give cos=1, sin=0)
        tb_angle <= "0000000000000000";
        tb_start <= '1';
        wait for 10 ns;
        tb_start <= '0';
        wait until tb_done = '1';
        wait for 20 ns;

        -- Test angle = 45 degrees (pi/4)
        tb_angle <= "0011001001000100";  -- ~0.785 radians scaled
        tb_start <= '1';
        wait for 10 ns;
        tb_start <= '0';
        wait until tb_done = '1';
        wait for 20 ns;

        -- Test angle = 90 degrees (pi/2)
        tb_angle <= "0110010010001000";  -- ~1.57 radians scaled
        tb_start <= '1';
        wait for 10 ns;
        tb_start <= '0';
        wait until tb_done = '1';
        wait for 20 ns;

        wait;
    end process;
end Test;
