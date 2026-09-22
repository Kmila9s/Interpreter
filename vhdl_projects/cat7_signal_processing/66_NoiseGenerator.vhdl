-- ============================================================================
-- VHDL Project: NoiseGenerator
-- Category: Signal Processing
-- Complexity: Medium
-- Description: LFSR-based noise generators (white, pink, Gaussian approximation)
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity WhiteNoiseGenerator is
    generic (
        DATA_WIDTH : integer := 16;
        LFSR_WIDTH : integer := 31  -- Maximal length LFSR
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        enable    : in  bit;
        seed      : in  bit_vector(LFSR_WIDTH-1 downto 0);
        load_seed : in  bit;
        noise_out : out bit_vector(DATA_WIDTH-1 downto 0);
        valid     : out bit
    );
end WhiteNoiseGenerator;

architecture Behavioral of WhiteNoiseGenerator is
    signal lfsr : bit_vector(LFSR_WIDTH-1 downto 0);
    signal feedback : bit;
begin
    -- 31-bit LFSR with taps at positions 31 and 28 (maximal length)
    feedback <= lfsr(30) xor lfsr(27);

    process(clk, reset)
    begin
        if reset = '1' then
            lfsr <= (0 => '1', others => '0');  -- Non-zero seed
            noise_out <= (others => '0');
            valid <= '0';
        elsif clk'event and clk = '1' then
            valid <= '0';

            if load_seed = '1' then
                if seed = (seed'range => '0') then
                    lfsr <= (0 => '1', others => '0');  -- Prevent all-zeros
                else
                    lfsr <= seed;
                end if;
            elsif enable = '1' then
                lfsr <= lfsr(LFSR_WIDTH-2 downto 0) & feedback;
                noise_out <= lfsr(DATA_WIDTH-1 downto 0);
                valid <= '1';
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Pink Noise Generator (1/f noise using Voss-McCartney algorithm)
-- ============================================================================
entity PinkNoiseGenerator is
    generic (
        DATA_WIDTH : integer := 16;
        NUM_OCTAVES : integer := 8
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        enable    : in  bit;
        noise_out : out bit_vector(DATA_WIDTH-1 downto 0);
        valid     : out bit
    );
end PinkNoiseGenerator;

architecture Behavioral of PinkNoiseGenerator is
    type lfsr_array is array (0 to NUM_OCTAVES-1) of bit_vector(15 downto 0);
    type value_array is array (0 to NUM_OCTAVES-1) of bit_vector(DATA_WIDTH-1 downto 0);

    signal lfsrs : lfsr_array;
    signal octave_values : value_array;
    signal counter : bit_vector(NUM_OCTAVES-1 downto 0);

    function lfsr_next(lfsr : bit_vector) return bit_vector is
        variable fb : bit;
        variable result : bit_vector(15 downto 0);
    begin
        fb := lfsr(15) xor lfsr(14) xor lfsr(12) xor lfsr(3);
        result := lfsr(14 downto 0) & fb;
        return result;
    end function;

    function to_signed_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable weight : integer := 1;
    begin
        for i in 0 to bv'length-2 loop
            if bv(i) = '1' then result := result + weight; end if;
            weight := weight * 2;
        end loop;
        if bv(bv'length-1) = '1' then result := result - weight; end if;
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

begin
    process(clk, reset)
        variable cnt_int : integer;
        variable sum : integer;
        variable updated_bit : integer;
    begin
        if reset = '1' then
            for i in 0 to NUM_OCTAVES-1 loop
                lfsrs(i) <= to_bv(12345 + i * 7919, 16);  -- Different seeds
                octave_values(i) <= (others => '0');
            end loop;
            counter <= (others => '0');
            noise_out <= (others => '0');
            valid <= '0';
        elsif clk'event and clk = '1' then
            valid <= '0';

            if enable = '1' then
                cnt_int := to_int(counter);

                -- Update octave values based on trailing zeros in counter
                -- This gives different update rates for each octave
                for i in 0 to NUM_OCTAVES-1 loop
                    if (cnt_int mod (2 ** (i+1))) = 0 then
                        lfsrs(i) <= lfsr_next(lfsrs(i));
                        octave_values(i) <= lfsrs(i)(DATA_WIDTH-1 downto 0);
                    end if;
                end loop;

                -- Sum all octaves
                sum := 0;
                for i in 0 to NUM_OCTAVES-1 loop
                    sum := sum + to_signed_int(octave_values(i));
                end loop;

                -- Scale down to prevent overflow
                noise_out <= to_bv(sum / NUM_OCTAVES, DATA_WIDTH);
                valid <= '1';

                -- Increment counter
                counter <= to_bv(cnt_int + 1, NUM_OCTAVES);
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Gaussian Noise Approximation (Central Limit Theorem using multiple LFSRs)
-- ============================================================================
entity GaussianNoiseGenerator is
    generic (
        DATA_WIDTH  : integer := 16;
        NUM_SOURCES : integer := 12  -- More sources = better approximation
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        enable    : in  bit;
        noise_out : out bit_vector(DATA_WIDTH-1 downto 0);
        valid     : out bit
    );
end GaussianNoiseGenerator;

architecture Behavioral of GaussianNoiseGenerator is
    type lfsr_array is array (0 to NUM_SOURCES-1) of bit_vector(15 downto 0);
    signal lfsrs : lfsr_array;

    -- Different feedback taps for variety
    type tap_array is array (0 to NUM_SOURCES-1) of integer;
    constant TAPS : tap_array := (0, 1, 2, 3, 5, 6, 7, 9, 10, 11, 12, 14);

    function lfsr_step(lfsr : bit_vector; tap : integer) return bit_vector is
        variable fb : bit;
        variable result : bit_vector(15 downto 0);
    begin
        fb := lfsr(15) xor lfsr(tap);
        result := lfsr(14 downto 0) & fb;
        return result;
    end function;

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
        variable sum : integer;
        variable mean_offset : integer;
    begin
        if reset = '1' then
            for i in 0 to NUM_SOURCES-1 loop
                lfsrs(i) <= to_bv(1 + i * 4099, 16);  -- Prime-based seeds
            end loop;
            noise_out <= (others => '0');
            valid <= '0';
        elsif clk'event and clk = '1' then
            valid <= '0';

            if enable = '1' then
                -- Step all LFSRs
                for i in 0 to NUM_SOURCES-1 loop
                    lfsrs(i) <= lfsr_step(lfsrs(i), TAPS(i));
                end loop;

                -- Sum uniform random values (CLT approximation)
                sum := 0;
                for i in 0 to NUM_SOURCES-1 loop
                    sum := sum + to_int(lfsrs(i)(7 downto 0));  -- Use 8 bits each
                end loop;

                -- Subtract mean to center at zero
                mean_offset := NUM_SOURCES * 128;  -- 128 is mean of 8-bit value
                sum := sum - mean_offset;

                -- Scale to output range
                sum := sum * 4;  -- Adjust variance

                -- Saturate
                if sum > 32767 then sum := 32767;
                elsif sum < -32768 then sum := -32768;
                end if;

                noise_out <= to_bv(sum, DATA_WIDTH);
                valid <= '1';
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Configurable Multi-mode Noise Generator
-- ============================================================================
entity NoiseGeneratorMulti is
    generic (DATA_WIDTH : integer := 16);
    port (
        clk        : in  bit;
        reset      : in  bit;
        enable     : in  bit;
        mode       : in  bit_vector(1 downto 0);  -- 00=white, 01=pink, 10=gaussian
        amplitude  : in  bit_vector(7 downto 0);  -- Output scaling
        noise_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid      : out bit
    );
end NoiseGeneratorMulti;

architecture Behavioral of NoiseGeneratorMulti is
    signal white_lfsr : bit_vector(30 downto 0);
    signal pink_lfsrs : bit_vector(127 downto 0);
    signal pink_values : bit_vector(127 downto 0);
    signal gauss_lfsrs : bit_vector(191 downto 0);
    signal counter : bit_vector(7 downto 0);

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

    function to_signed_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable weight : integer := 1;
    begin
        for i in 0 to bv'length-2 loop
            if bv(i) = '1' then result := result + weight; end if;
            weight := weight * 2;
        end loop;
        if bv(bv'length-1) = '1' then result := result - weight; end if;
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
        variable fb : bit;
        variable raw_noise : integer;
        variable scaled : integer;
        variable amp : integer;
    begin
        if reset = '1' then
            white_lfsr <= (0 => '1', others => '0');
            pink_lfsrs <= (others => '1');
            pink_values <= (others => '0');
            gauss_lfsrs <= (others => '1');
            counter <= (others => '0');
            noise_out <= (others => '0');
            valid <= '0';
        elsif clk'event and clk = '1' then
            valid <= '0';

            if enable = '1' then
                amp := to_int(amplitude);

                case mode is
                    when "00" =>  -- White noise
                        fb := white_lfsr(30) xor white_lfsr(27);
                        white_lfsr <= white_lfsr(29 downto 0) & fb;
                        raw_noise := to_signed_int(white_lfsr(DATA_WIDTH-1 downto 0));

                    when "01" =>  -- Pink noise (simplified)
                        -- Update different octaves at different rates
                        for i in 0 to 7 loop
                            if counter(i) = '1' then
                                fb := pink_lfsrs(i*16+15) xor pink_lfsrs(i*16+13);
                                pink_lfsrs(i*16+15 downto i*16) <=
                                    pink_lfsrs(i*16+14 downto i*16) & fb;
                                pink_values(i*16+15 downto i*16) <=
                                    pink_lfsrs(i*16+15 downto i*16);
                            end if;
                        end loop;
                        raw_noise := 0;
                        for i in 0 to 7 loop
                            raw_noise := raw_noise +
                                to_signed_int(pink_values(i*16+15 downto i*16));
                        end loop;
                        raw_noise := raw_noise / 8;

                    when "10" =>  -- Gaussian approximation
                        raw_noise := 0;
                        for i in 0 to 11 loop
                            fb := gauss_lfsrs(i*16+15) xor gauss_lfsrs(i*16+13);
                            gauss_lfsrs(i*16+15 downto i*16) <=
                                gauss_lfsrs(i*16+14 downto i*16) & fb;
                            raw_noise := raw_noise + to_int(gauss_lfsrs(i*16+7 downto i*16));
                        end loop;
                        raw_noise := (raw_noise - 12 * 128) * 4;

                    when others =>
                        raw_noise := 0;
                end case;

                -- Apply amplitude scaling
                scaled := (raw_noise * amp) / 256;

                -- Saturate
                if scaled > 32767 then scaled := 32767;
                elsif scaled < -32768 then scaled := -32768;
                end if;

                noise_out <= to_bv(scaled, DATA_WIDTH);
                valid <= '1';

                counter <= to_bv(to_int(counter) + 1, 8);
            end if;
        end if;
    end process;
end Behavioral;
