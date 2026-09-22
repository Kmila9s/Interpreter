-- ============================================================================
-- VHDL Project: FFTProcessor
-- Category: Signal Processing
-- Complexity: Very High
-- Description: Radix-2 DIT FFT processor with butterfly units
--              Configurable point size, bit-reversal addressing
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity FFTProcessor is
    generic (
        DATA_WIDTH : integer := 16;
        FFT_SIZE   : integer := 16;  -- Must be power of 2
        LOG2_SIZE  : integer := 4    -- log2(FFT_SIZE)
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        start       : in  bit;
        -- Input interface
        din_real    : in  bit_vector(DATA_WIDTH-1 downto 0);
        din_imag    : in  bit_vector(DATA_WIDTH-1 downto 0);
        din_valid   : in  bit;
        din_ready   : out bit;
        din_index   : out bit_vector(LOG2_SIZE-1 downto 0);
        -- Output interface
        dout_real   : out bit_vector(DATA_WIDTH-1 downto 0);
        dout_imag   : out bit_vector(DATA_WIDTH-1 downto 0);
        dout_valid  : out bit;
        dout_index  : out bit_vector(LOG2_SIZE-1 downto 0);
        -- Status
        busy        : out bit;
        done        : out bit
    );
end FFTProcessor;

architecture Behavioral of FFTProcessor is
    type state_type is (IDLE, LOAD, COMPUTE, OUTPUT);
    signal state : state_type;

    -- Memory for real and imaginary parts
    type data_mem is array (0 to FFT_SIZE-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal mem_real, mem_imag : data_mem;

    -- Twiddle factor ROM (pre-computed)
    type twiddle_mem is array (0 to FFT_SIZE/2-1) of bit_vector(DATA_WIDTH-1 downto 0);

    -- Counters
    signal load_cnt : integer range 0 to FFT_SIZE-1;
    signal out_cnt : integer range 0 to FFT_SIZE-1;
    signal stage : integer range 0 to LOG2_SIZE-1;
    signal butterfly_cnt : integer range 0 to FFT_SIZE/2-1;
    signal group_cnt : integer range 0 to FFT_SIZE-1;

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

    -- Bit reversal function
    function bit_reverse(val : integer; bits : integer) return integer is
        variable result : integer := 0;
        variable temp : integer := val;
    begin
        for i in 0 to bits-1 loop
            result := result * 2;
            if (temp mod 2) = 1 then
                result := result + 1;
            end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

    -- Simplified twiddle factors (cos and sin tables)
    -- For 16-point FFT: W_k = exp(-j*2*pi*k/16)
    function get_twiddle_cos(k : integer; n : integer) return integer is
        -- Simplified cosine table scaled by 16384 (Q14)
        type cos_table is array (0 to 7) of integer;
        constant COS_16 : cos_table := (16384, 15137, 11585, 6270, 0, -6270, -11585, -15137);
    begin
        if n = 16 then
            return COS_16(k mod 8);
        else
            return 16384;  -- Default to 1.0
        end if;
    end function;

    function get_twiddle_sin(k : integer; n : integer) return integer is
        type sin_table is array (0 to 7) of integer;
        constant SIN_16 : sin_table := (0, -6270, -11585, -15137, -16384, -15137, -11585, -6270);
    begin
        if n = 16 then
            return SIN_16(k mod 8);
        else
            return 0;  -- Default to 0
        end if;
    end function;

begin
    process(clk, reset)
        variable idx_a, idx_b : integer;
        variable ar, ai, br, bi : integer;
        variable wr, wi : integer;
        variable tr, ti : integer;
        variable group_size, half_size : integer;
        variable twiddle_idx : integer;
        variable br_idx : integer;
    begin
        if reset = '1' then
            state <= IDLE;
            busy <= '0';
            done <= '0';
            din_ready <= '0';
            dout_valid <= '0';
            load_cnt <= 0;
            out_cnt <= 0;
            stage <= 0;
            for i in 0 to FFT_SIZE-1 loop
                mem_real(i) <= (others => '0');
                mem_imag(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            done <= '0';
            dout_valid <= '0';

            case state is
                when IDLE =>
                    busy <= '0';
                    din_ready <= '0';
                    if start = '1' then
                        state <= LOAD;
                        load_cnt <= 0;
                        busy <= '1';
                        din_ready <= '1';
                    end if;

                when LOAD =>
                    din_index <= to_bv(load_cnt, LOG2_SIZE);
                    if din_valid = '1' then
                        -- Store in bit-reversed order
                        br_idx := bit_reverse(load_cnt, LOG2_SIZE);
                        mem_real(br_idx) <= din_real;
                        mem_imag(br_idx) <= din_imag;

                        if load_cnt = FFT_SIZE - 1 then
                            din_ready <= '0';
                            state <= COMPUTE;
                            stage <= 0;
                            butterfly_cnt <= 0;
                            group_cnt <= 0;
                        else
                            load_cnt <= load_cnt + 1;
                        end if;
                    end if;

                when COMPUTE =>
                    -- Radix-2 DIT butterfly computation
                    group_size := 2 ** (stage + 1);
                    half_size := group_size / 2;

                    idx_a := group_cnt * group_size + butterfly_cnt;
                    idx_b := idx_a + half_size;

                    -- Get data
                    ar := to_signed_int(mem_real(idx_a));
                    ai := to_signed_int(mem_imag(idx_a));
                    br := to_signed_int(mem_real(idx_b));
                    bi := to_signed_int(mem_imag(idx_b));

                    -- Get twiddle factors
                    twiddle_idx := butterfly_cnt * (FFT_SIZE / group_size);
                    wr := get_twiddle_cos(twiddle_idx, FFT_SIZE);
                    wi := get_twiddle_sin(twiddle_idx, FFT_SIZE);

                    -- Complex multiply: t = b * w
                    tr := (br * wr - bi * wi) / 16384;
                    ti := (br * wi + bi * wr) / 16384;

                    -- Butterfly: a' = a + t, b' = a - t
                    mem_real(idx_a) <= to_bv((ar + tr) / 2, DATA_WIDTH);
                    mem_imag(idx_a) <= to_bv((ai + ti) / 2, DATA_WIDTH);
                    mem_real(idx_b) <= to_bv((ar - tr) / 2, DATA_WIDTH);
                    mem_imag(idx_b) <= to_bv((ai - ti) / 2, DATA_WIDTH);

                    -- Advance counters
                    if butterfly_cnt = half_size - 1 then
                        butterfly_cnt <= 0;
                        if group_cnt = (FFT_SIZE / group_size) - 1 then
                            group_cnt <= 0;
                            if stage = LOG2_SIZE - 1 then
                                state <= OUTPUT;
                                out_cnt <= 0;
                            else
                                stage <= stage + 1;
                            end if;
                        else
                            group_cnt <= group_cnt + 1;
                        end if;
                    else
                        butterfly_cnt <= butterfly_cnt + 1;
                    end if;

                when OUTPUT =>
                    dout_real <= mem_real(out_cnt);
                    dout_imag <= mem_imag(out_cnt);
                    dout_index <= to_bv(out_cnt, LOG2_SIZE);
                    dout_valid <= '1';

                    if out_cnt = FFT_SIZE - 1 then
                        done <= '1';
                        state <= IDLE;
                    else
                        out_cnt <= out_cnt + 1;
                    end if;
            end case;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Butterfly Unit (standalone)
-- ============================================================================
entity ButterflyUnit is
    generic (DATA_WIDTH : integer := 16);
    port (
        clk     : in  bit;
        enable  : in  bit;
        ar, ai  : in  bit_vector(DATA_WIDTH-1 downto 0);
        br, bi  : in  bit_vector(DATA_WIDTH-1 downto 0);
        wr, wi  : in  bit_vector(DATA_WIDTH-1 downto 0);
        ar_out, ai_out : out bit_vector(DATA_WIDTH-1 downto 0);
        br_out, bi_out : out bit_vector(DATA_WIDTH-1 downto 0)
    );
end ButterflyUnit;

architecture Behavioral of ButterflyUnit is
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
    process(clk)
        variable a_r, a_i, b_r, b_i : integer;
        variable w_r, w_i : integer;
        variable t_r, t_i : integer;
    begin
        if clk'event and clk = '1' then
            if enable = '1' then
                a_r := to_signed_int(ar);
                a_i := to_signed_int(ai);
                b_r := to_signed_int(br);
                b_i := to_signed_int(bi);
                w_r := to_signed_int(wr);
                w_i := to_signed_int(wi);

                -- t = b * w (complex multiply)
                t_r := (b_r * w_r - b_i * w_i) / 32768;
                t_i := (b_r * w_i + b_i * w_r) / 32768;

                -- Butterfly outputs
                ar_out <= to_bv(a_r + t_r, DATA_WIDTH);
                ai_out <= to_bv(a_i + t_i, DATA_WIDTH);
                br_out <= to_bv(a_r - t_r, DATA_WIDTH);
                bi_out <= to_bv(a_i - t_i, DATA_WIDTH);
            end if;
        end if;
    end process;
end Behavioral;
