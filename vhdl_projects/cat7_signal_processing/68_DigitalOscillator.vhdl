-- ============================================================================
-- VHDL Project: DigitalOscillator
-- Category: Signal Processing
-- Complexity: Medium-High
-- Description: Direct Digital Synthesis (DDS) / Numerically Controlled Oscillator
--              Phase accumulator with lookup table and interpolation
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity DDSOscillator is
    generic (
        PHASE_WIDTH : integer := 32;
        OUTPUT_WIDTH : integer := 16;
        TABLE_BITS : integer := 10  -- 1024 entry lookup table
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        enable     : in  bit;
        -- Frequency control (phase increment per clock)
        freq_word  : in  bit_vector(PHASE_WIDTH-1 downto 0);
        -- Phase offset
        phase_off  : in  bit_vector(PHASE_WIDTH-1 downto 0);
        -- Outputs
        sine_out   : out bit_vector(OUTPUT_WIDTH-1 downto 0);
        cosine_out : out bit_vector(OUTPUT_WIDTH-1 downto 0);
        valid      : out bit
    );
end DDSOscillator;

architecture Behavioral of DDSOscillator is
    signal phase_acc : bit_vector(PHASE_WIDTH-1 downto 0);

    -- Quarter-wave sine table (stores only 0-90 degrees)
    -- 256 entries for efficiency, scaled to 32767
    type sine_table_type is array (0 to 255) of integer;
    constant SINE_TABLE : sine_table_type := (
            0,   402,   804,  1206,  1608,  2009,  2410,  2811,
         3212,  3612,  4011,  4410,  4808,  5205,  5602,  5998,
         6393,  6786,  7179,  7571,  7962,  8351,  8739,  9126,
         9512,  9896, 10278, 10659, 11039, 11417, 11793, 12167,
        12539, 12910, 13279, 13645, 14010, 14372, 14732, 15090,
        15446, 15800, 16151, 16499, 16846, 17189, 17530, 17869,
        18204, 18537, 18868, 19195, 19519, 19841, 20159, 20475,
        20787, 21096, 21403, 21705, 22005, 22301, 22594, 22884,
        23170, 23452, 23731, 24007, 24279, 24547, 24812, 25073,
        25330, 25583, 25832, 26077, 26319, 26556, 26790, 27019,
        27245, 27466, 27683, 27896, 28105, 28310, 28510, 28706,
        28898, 29085, 29268, 29447, 29621, 29791, 29956, 30117,
        30273, 30424, 30571, 30714, 30852, 30985, 31113, 31237,
        31356, 31470, 31580, 31685, 31785, 31880, 31971, 32057,
        32137, 32213, 32285, 32351, 32412, 32469, 32521, 32567,
        32609, 32646, 32678, 32705, 32728, 32745, 32757, 32765,
        32767, 32765, 32757, 32745, 32728, 32705, 32678, 32646,
        32609, 32567, 32521, 32469, 32412, 32351, 32285, 32213,
        32137, 32057, 31971, 31880, 31785, 31685, 31580, 31470,
        31356, 31237, 31113, 30985, 30852, 30714, 30571, 30424,
        30273, 30117, 29956, 29791, 29621, 29447, 29268, 29085,
        28898, 28706, 28510, 28310, 28105, 27896, 27683, 27466,
        27245, 27019, 26790, 26556, 26319, 26077, 25832, 25583,
        25330, 25073, 24812, 24547, 24279, 24007, 23731, 23452,
        23170, 22884, 22594, 22301, 22005, 21705, 21403, 21096,
        20787, 20475, 20159, 19841, 19519, 19195, 18868, 18537,
        18204, 17869, 17530, 17189, 16846, 16499, 16151, 15800,
        15446, 15090, 14732, 14372, 14010, 13645, 13279, 12910,
        12539, 12167, 11793, 11417, 11039, 10659, 10278,  9896,
         9512,  9126,  8739,  8351,  7962,  7571,  7179,  6786,
         6393,  5998,  5602,  5205,  4808,  4410,  4011,  3612,
         3212,  2811,  2410,  2009,  1608,  1206,   804,   402
    );

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then result := result + power; end if;
            power := power * 2;
        end loop;
        return result;
    end function;

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer;
    begin
        if val < 0 then temp := val + (2 ** width);
        else temp := val; end if;
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

    function add_bv(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable carry : bit := '0';
        variable sum : integer;
    begin
        for i in a'low to a'high loop
            sum := 0;
            if a(i) = '1' then sum := sum + 1; end if;
            if b(i) = '1' then sum := sum + 1; end if;
            if carry = '1' then sum := sum + 1; end if;
            if sum >= 2 then carry := '1'; else carry := '0'; end if;
            if (sum mod 2) = 1 then result(i) := '1'; else result(i) := '0'; end if;
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
        variable phase_total : bit_vector(PHASE_WIDTH-1 downto 0);
        variable table_idx : integer;
        variable quadrant : integer;
        variable sine_val, cosine_val : integer;
    begin
        if reset = '1' then
            phase_acc <= (others => '0');
            sine_out <= (others => '0');
            cosine_out <= (others => '0');
            valid <= '0';
        elsif clk'event and clk = '1' then
            valid <= '0';

            if enable = '1' then
                -- Update phase accumulator
                phase_acc <= add_bv(phase_acc, freq_word);

                -- Add phase offset
                phase_total := add_bv(phase_acc, phase_off);

                -- Extract quadrant (top 2 bits)
                quadrant := to_int(phase_total(PHASE_WIDTH-1 downto PHASE_WIDTH-2));

                -- Extract table index (next 8 bits)
                table_idx := to_int(phase_total(PHASE_WIDTH-3 downto PHASE_WIDTH-10));

                -- Lookup with quadrant handling for full 360 degrees
                case quadrant is
                    when 0 =>  -- 0-90 degrees
                        sine_val := SINE_TABLE(table_idx);
                        cosine_val := SINE_TABLE(255 - table_idx);
                    when 1 =>  -- 90-180 degrees
                        sine_val := SINE_TABLE(255 - table_idx);
                        cosine_val := -SINE_TABLE(table_idx);
                    when 2 =>  -- 180-270 degrees
                        sine_val := -SINE_TABLE(table_idx);
                        cosine_val := -SINE_TABLE(255 - table_idx);
                    when others =>  -- 270-360 degrees
                        sine_val := -SINE_TABLE(255 - table_idx);
                        cosine_val := SINE_TABLE(table_idx);
                end case;

                sine_out <= to_bv(sine_val, OUTPUT_WIDTH);
                cosine_out <= to_bv(cosine_val, OUTPUT_WIDTH);
                valid <= '1';
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Multi-waveform NCO
-- ============================================================================
entity MultiWaveNCO is
    generic (
        PHASE_WIDTH  : integer := 24;
        OUTPUT_WIDTH : integer := 16
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        enable    : in  bit;
        freq_word : in  bit_vector(PHASE_WIDTH-1 downto 0);
        waveform  : in  bit_vector(2 downto 0);  -- 000=sine, 001=square, 010=saw, 011=triangle
        output    : out bit_vector(OUTPUT_WIDTH-1 downto 0);
        valid     : out bit
    );
end MultiWaveNCO;

architecture Behavioral of MultiWaveNCO is
    signal phase_acc : bit_vector(PHASE_WIDTH-1 downto 0);

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then result := result + power; end if;
            power := power * 2;
        end loop;
        return result;
    end function;

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer;
    begin
        if val < 0 then temp := val + (2 ** width);
        else temp := val; end if;
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
        variable phase : integer;
        variable out_val : integer;
        variable half_phase : integer;
    begin
        if reset = '1' then
            phase_acc <= (others => '0');
            output <= (others => '0');
            valid <= '0';
        elsif clk'event and clk = '1' then
            valid <= '0';

            if enable = '1' then
                -- Update phase
                phase_acc <= to_bv(to_int(phase_acc) + to_int(freq_word), PHASE_WIDTH);
                phase := to_int(phase_acc);
                half_phase := 2 ** (PHASE_WIDTH - 1);

                case waveform is
                    when "000" =>  -- Sine (simplified approximation)
                        -- Parabolic approximation: 4*x*(1-x) for 0-0.5, mirrored
                        if phase < half_phase then
                            out_val := (phase * (half_phase - phase)) / (half_phase / 4);
                            out_val := out_val - 32768;
                        else
                            out_val := ((phase - half_phase) * (2*half_phase - phase - half_phase)) / (half_phase / 4);
                            out_val := 32768 - out_val;
                        end if;

                    when "001" =>  -- Square
                        if phase < half_phase then
                            out_val := 32767;
                        else
                            out_val := -32768;
                        end if;

                    when "010" =>  -- Sawtooth
                        out_val := (phase * 65536 / (2 ** PHASE_WIDTH)) - 32768;

                    when "011" =>  -- Triangle
                        if phase < half_phase then
                            out_val := (phase * 65536 / half_phase) - 32768;
                        else
                            out_val := 32768 - ((phase - half_phase) * 65536 / half_phase);
                        end if;

                    when "100" =>  -- Pulse (25% duty)
                        if phase < (2 ** (PHASE_WIDTH - 2)) then
                            out_val := 32767;
                        else
                            out_val := -32768;
                        end if;

                    when others =>
                        out_val := 0;
                end case;

                -- Clamp output
                if out_val > 32767 then out_val := 32767;
                elsif out_val < -32768 then out_val := -32768;
                end if;

                output <= to_bv(out_val, OUTPUT_WIDTH);
                valid <= '1';
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Frequency Modulated Oscillator
-- ============================================================================
entity FMOscillator is
    generic (
        PHASE_WIDTH : integer := 32;
        OUTPUT_WIDTH : integer := 16
    );
    port (
        clk          : in  bit;
        reset        : in  bit;
        enable       : in  bit;
        carrier_freq : in  bit_vector(PHASE_WIDTH-1 downto 0);
        mod_freq     : in  bit_vector(PHASE_WIDTH-1 downto 0);
        mod_depth    : in  bit_vector(15 downto 0);  -- Modulation index
        output       : out bit_vector(OUTPUT_WIDTH-1 downto 0);
        valid        : out bit
    );
end FMOscillator;

architecture Behavioral of FMOscillator is
    signal carrier_phase : bit_vector(PHASE_WIDTH-1 downto 0);
    signal mod_phase : bit_vector(PHASE_WIDTH-1 downto 0);

    type sine_lut is array (0 to 255) of integer;
    constant SINE : sine_lut := (
            0,   804,  1608,  2410,  3212,  4011,  4808,  5602,
         6393,  7179,  7962,  8739,  9512, 10278, 11039, 11793,
        12539, 13279, 14010, 14732, 15446, 16151, 16846, 17530,
        18204, 18868, 19519, 20159, 20787, 21403, 22005, 22594,
        23170, 23731, 24279, 24812, 25330, 25832, 26319, 26790,
        27245, 27683, 28105, 28510, 28898, 29268, 29621, 29956,
        30273, 30571, 30852, 31113, 31356, 31580, 31785, 31971,
        32137, 32285, 32412, 32521, 32609, 32678, 32728, 32757,
        32767, 32757, 32728, 32678, 32609, 32521, 32412, 32285,
        32137, 31971, 31785, 31580, 31356, 31113, 30852, 30571,
        30273, 29956, 29621, 29268, 28898, 28510, 28105, 27683,
        27245, 26790, 26319, 25832, 25330, 24812, 24279, 23731,
        23170, 22594, 22005, 21403, 20787, 20159, 19519, 18868,
        18204, 17530, 16846, 16151, 15446, 14732, 14010, 13279,
        12539, 11793, 11039, 10278,  9512,  8739,  7962,  7179,
         6393,  5602,  4808,  4011,  3212,  2410,  1608,   804,
            0,  -804, -1608, -2410, -3212, -4011, -4808, -5602,
        -6393, -7179, -7962, -8739, -9512,-10278,-11039,-11793,
       -12539,-13279,-14010,-14732,-15446,-16151,-16846,-17530,
       -18204,-18868,-19519,-20159,-20787,-21403,-22005,-22594,
       -23170,-23731,-24279,-24812,-25330,-25832,-26319,-26790,
       -27245,-27683,-28105,-28510,-28898,-29268,-29621,-29956,
       -30273,-30571,-30852,-31113,-31356,-31580,-31785,-31971,
       -32137,-32285,-32412,-32521,-32609,-32678,-32728,-32757,
       -32767,-32757,-32728,-32678,-32609,-32521,-32412,-32285,
       -32137,-31971,-31785,-31580,-31356,-31113,-30852,-30571,
       -30273,-29956,-29621,-29268,-28898,-28510,-28105,-27683,
       -27245,-26790,-26319,-25832,-25330,-24812,-24279,-23731,
       -23170,-22594,-22005,-21403,-20787,-20159,-19519,-18868,
       -18204,-17530,-16846,-16151,-15446,-14732,-14010,-13279,
       -12539,-11793,-11039,-10278, -9512, -8739, -7962, -7179,
        -6393, -5602, -4808, -4011, -3212, -2410, -1608,  -804
    );

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then result := result + power; end if;
            power := power * 2;
        end loop;
        return result;
    end function;

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer;
    begin
        if val < 0 then temp := val + (2 ** width);
        else temp := val; end if;
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
        variable mod_idx : integer;
        variable mod_val : integer;
        variable phase_mod : integer;
        variable carrier_idx : integer;
        variable out_val : integer;
        variable depth : integer;
    begin
        if reset = '1' then
            carrier_phase <= (others => '0');
            mod_phase <= (others => '0');
            output <= (others => '0');
            valid <= '0';
        elsif clk'event and clk = '1' then
            valid <= '0';

            if enable = '1' then
                -- Update modulator phase
                mod_phase <= to_bv(to_int(mod_phase) + to_int(mod_freq), PHASE_WIDTH);
                mod_idx := to_int(mod_phase(PHASE_WIDTH-1 downto PHASE_WIDTH-8));
                mod_val := SINE(mod_idx);

                -- Calculate phase modulation
                depth := to_int(mod_depth);
                phase_mod := (mod_val * depth) / 32768;

                -- Update carrier phase with modulation
                carrier_phase <= to_bv(to_int(carrier_phase) + to_int(carrier_freq) + phase_mod, PHASE_WIDTH);

                -- Get carrier output
                carrier_idx := to_int(carrier_phase(PHASE_WIDTH-1 downto PHASE_WIDTH-8));
                out_val := SINE(carrier_idx);

                output <= to_bv(out_val, OUTPUT_WIDTH);
                valid <= '1';
            end if;
        end if;
    end process;
end Behavioral;
