-- ============================================================================
-- VHDL Project: SignalMixer
-- Category: Signal Processing
-- Complexity: Medium
-- Description: Multi-channel audio/signal mixer with gain control
--              Volume control, pan, and crossfade capabilities
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity SignalMixer is
    generic (
        DATA_WIDTH   : integer := 16;
        NUM_CHANNELS : integer := 4
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        -- Input channels
        ch0_data   : in  bit_vector(DATA_WIDTH-1 downto 0);
        ch1_data   : in  bit_vector(DATA_WIDTH-1 downto 0);
        ch2_data   : in  bit_vector(DATA_WIDTH-1 downto 0);
        ch3_data   : in  bit_vector(DATA_WIDTH-1 downto 0);
        ch_valid   : in  bit;
        -- Gain controls (0-255, 128 = unity)
        ch0_gain   : in  bit_vector(7 downto 0);
        ch1_gain   : in  bit_vector(7 downto 0);
        ch2_gain   : in  bit_vector(7 downto 0);
        ch3_gain   : in  bit_vector(7 downto 0);
        -- Master output
        master_gain : in  bit_vector(7 downto 0);
        mix_out    : out bit_vector(DATA_WIDTH-1 downto 0);
        out_valid  : out bit;
        -- Clipping indicator
        clip       : out bit
    );
end SignalMixer;

architecture Behavioral of SignalMixer is
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
        variable ch0, ch1, ch2, ch3 : integer;
        variable g0, g1, g2, g3, gm : integer;
        variable mix : integer;
        variable clipped : bit;
    begin
        if reset = '1' then
            mix_out <= (others => '0');
            out_valid <= '0';
            clip <= '0';
        elsif clk'event and clk = '1' then
            out_valid <= '0';
            clip <= '0';

            if ch_valid = '1' then
                -- Get channel data
                ch0 := to_signed_int(ch0_data);
                ch1 := to_signed_int(ch1_data);
                ch2 := to_signed_int(ch2_data);
                ch3 := to_signed_int(ch3_data);

                -- Get gains
                g0 := to_int(ch0_gain);
                g1 := to_int(ch1_gain);
                g2 := to_int(ch2_gain);
                g3 := to_int(ch3_gain);
                gm := to_int(master_gain);

                -- Apply channel gains and sum
                mix := (ch0 * g0 + ch1 * g1 + ch2 * g2 + ch3 * g3) / 128;

                -- Apply master gain
                mix := (mix * gm) / 128;

                -- Soft clipping / saturation
                clipped := '0';
                if mix > 32767 then
                    mix := 32767;
                    clipped := '1';
                elsif mix < -32768 then
                    mix := -32768;
                    clipped := '1';
                end if;

                mix_out <= to_bv(mix, DATA_WIDTH);
                out_valid <= '1';
                clip <= clipped;
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Stereo Mixer with Pan Control
-- ============================================================================
entity StereoMixer is
    generic (
        DATA_WIDTH   : integer := 16;
        NUM_CHANNELS : integer := 4
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        -- Mono input channels
        ch0_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        ch1_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        ch2_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        ch3_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in   : in  bit;
        -- Gain (0-255)
        ch0_gain, ch1_gain, ch2_gain, ch3_gain : in bit_vector(7 downto 0);
        -- Pan (-128 = full left, 0 = center, +127 = full right)
        ch0_pan, ch1_pan, ch2_pan, ch3_pan : in bit_vector(7 downto 0);
        -- Stereo output
        left_out   : out bit_vector(DATA_WIDTH-1 downto 0);
        right_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out  : out bit
    );
end StereoMixer;

architecture Behavioral of StereoMixer is
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

    function saturate(val : integer) return integer is
    begin
        if val > 32767 then return 32767;
        elsif val < -32768 then return -32768;
        else return val;
        end if;
    end function;

begin
    process(clk, reset)
        variable ch, gain, pan : integer;
        variable left_gain, right_gain : integer;
        variable left_sum, right_sum : integer;
        variable left_contrib, right_contrib : integer;
        type ch_array is array (0 to 3) of integer;
        variable channels : ch_array;
        variable gains : ch_array;
        variable pans : ch_array;
    begin
        if reset = '1' then
            left_out <= (others => '0');
            right_out <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            if valid_in = '1' then
                -- Load channel data
                channels(0) := to_signed_int(ch0_in);
                channels(1) := to_signed_int(ch1_in);
                channels(2) := to_signed_int(ch2_in);
                channels(3) := to_signed_int(ch3_in);

                gains(0) := to_int(ch0_gain);
                gains(1) := to_int(ch1_gain);
                gains(2) := to_int(ch2_gain);
                gains(3) := to_int(ch3_gain);

                pans(0) := to_signed_int(ch0_pan);
                pans(1) := to_signed_int(ch1_pan);
                pans(2) := to_signed_int(ch2_pan);
                pans(3) := to_signed_int(ch3_pan);

                left_sum := 0;
                right_sum := 0;

                for i in 0 to 3 loop
                    ch := channels(i);
                    gain := gains(i);
                    pan := pans(i);

                    -- Calculate left/right gains from pan position
                    -- Pan: -128 to +127, Center = 0
                    if pan <= 0 then
                        left_gain := 128;
                        right_gain := 128 + pan;  -- Reduces from 128 to 0
                    else
                        left_gain := 128 - pan;   -- Reduces from 128 to 1
                        right_gain := 128;
                    end if;

                    -- Apply gain and pan
                    left_contrib := (ch * gain * left_gain) / (128 * 128);
                    right_contrib := (ch * gain * right_gain) / (128 * 128);

                    left_sum := left_sum + left_contrib;
                    right_sum := right_sum + right_contrib;
                end loop;

                left_out <= to_bv(saturate(left_sum), DATA_WIDTH);
                right_out <= to_bv(saturate(right_sum), DATA_WIDTH);
                valid_out <= '1';
            end if;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Crossfader (for DJ-style mixing)
-- ============================================================================
entity Crossfader is
    generic (DATA_WIDTH : integer := 16);
    port (
        clk       : in  bit;
        reset     : in  bit;
        -- Two stereo inputs
        a_left    : in  bit_vector(DATA_WIDTH-1 downto 0);
        a_right   : in  bit_vector(DATA_WIDTH-1 downto 0);
        b_left    : in  bit_vector(DATA_WIDTH-1 downto 0);
        b_right   : in  bit_vector(DATA_WIDTH-1 downto 0);
        valid_in  : in  bit;
        -- Crossfade position (0 = full A, 255 = full B)
        xfade     : in  bit_vector(7 downto 0);
        -- Curve type ('0' = linear, '1' = equal power approximation)
        curve     : in  bit;
        -- Output
        out_left  : out bit_vector(DATA_WIDTH-1 downto 0);
        out_right : out bit_vector(DATA_WIDTH-1 downto 0);
        valid_out : out bit
    );
end Crossfader;

architecture Behavioral of Crossfader is
    -- Equal power curve lookup (sqrt approximation)
    type curve_table is array (0 to 255) of integer;
    constant EQ_POWER : curve_table := (
        255, 255, 255, 255, 255, 254, 254, 254, 253, 253, 252, 252, 251, 251, 250, 249,
        249, 248, 247, 247, 246, 245, 244, 243, 242, 241, 240, 239, 238, 237, 236, 235,
        234, 233, 232, 230, 229, 228, 227, 225, 224, 223, 221, 220, 218, 217, 215, 214,
        212, 211, 209, 208, 206, 204, 203, 201, 199, 197, 196, 194, 192, 190, 188, 186,
        184, 182, 180, 178, 176, 174, 172, 170, 168, 166, 164, 162, 159, 157, 155, 153,
        150, 148, 146, 143, 141, 138, 136, 134, 131, 129, 126, 124, 121, 118, 116, 113,
        111, 108, 105, 103, 100,  97,  95,  92,  89,  86,  84,  81,  78,  75,  72,  69,
         66,  63,  60,  57,  54,  51,  48,  45,  42,  39,  36,  33,  30,  27,  24,  21,
         18,  15,  12,   9,   6,   3,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,
          0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,
          0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,
          0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,
          0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,
          0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,
          0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,
          0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0,   0
    );

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
        variable pos : integer;
        variable gain_a, gain_b : integer;
        variable al, ar, bl, br : integer;
        variable out_l, out_r : integer;
    begin
        if reset = '1' then
            out_left <= (others => '0');
            out_right <= (others => '0');
            valid_out <= '0';
        elsif clk'event and clk = '1' then
            valid_out <= '0';

            if valid_in = '1' then
                pos := to_int(xfade);

                if curve = '0' then
                    -- Linear crossfade
                    gain_a := 255 - pos;
                    gain_b := pos;
                else
                    -- Equal power approximation
                    gain_a := EQ_POWER(pos);
                    gain_b := EQ_POWER(255 - pos);
                end if;

                al := to_signed_int(a_left);
                ar := to_signed_int(a_right);
                bl := to_signed_int(b_left);
                br := to_signed_int(b_right);

                out_l := (al * gain_a + bl * gain_b) / 255;
                out_r := (ar * gain_a + br * gain_b) / 255;

                out_left <= to_bv(out_l, DATA_WIDTH);
                out_right <= to_bv(out_r, DATA_WIDTH);
                valid_out <= '1';
            end if;
        end if;
    end process;
end Behavioral;
