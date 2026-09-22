-- ============================================================================
-- VHDL Project: GrayCodec
-- Category: Digital Logic Fundamentals
-- Complexity: Medium
-- Description: Binary to Gray code and Gray to Binary converters
--              Configurable width, includes encoder and decoder
--              Gray code: only one bit changes between adjacent values
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity GrayCodec is
    generic (
        WIDTH : integer := 8
    );
    port (
        binary_in   : in  bit_vector(WIDTH-1 downto 0);
        gray_in     : in  bit_vector(WIDTH-1 downto 0);
        gray_out    : out bit_vector(WIDTH-1 downto 0);  -- Binary to Gray
        binary_out  : out bit_vector(WIDTH-1 downto 0)   -- Gray to Binary
    );
end GrayCodec;

architecture Behavioral of GrayCodec is
begin
    -- Binary to Gray conversion
    -- Gray(n) = Binary(n) XOR Binary(n+1) for n < WIDTH-1
    -- Gray(MSB) = Binary(MSB)
    bin_to_gray: process(binary_in)
    begin
        gray_out(WIDTH-1) <= binary_in(WIDTH-1);
        for i in 0 to WIDTH-2 loop
            gray_out(i) <= binary_in(i) xor binary_in(i+1);
        end loop;
    end process bin_to_gray;

    -- Gray to Binary conversion
    -- Binary(n) = XOR of all Gray bits from n to MSB
    gray_to_bin: process(gray_in)
        variable xor_chain : bit;
    begin
        xor_chain := '0';
        for i in WIDTH-1 downto 0 loop
            xor_chain := xor_chain xor gray_in(i);
            binary_out(i) <= xor_chain;
        end loop;
    end process gray_to_bin;

end Behavioral;

-- ============================================================================
-- Gray Code Counter (counts in Gray code sequence)
-- ============================================================================
entity GrayCounter is
    generic (
        WIDTH : integer := 8
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        enable      : in  bit;
        count_up    : in  bit;  -- '1' = up, '0' = down
        gray_count  : out bit_vector(WIDTH-1 downto 0);
        binary_count: out bit_vector(WIDTH-1 downto 0)
    );
end GrayCounter;

architecture Behavioral of GrayCounter is
    signal binary_reg : bit_vector(WIDTH-1 downto 0);
    signal gray_reg   : bit_vector(WIDTH-1 downto 0);

    -- Function to increment binary
    function increment(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable carry : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := val(i) xor carry;
            carry := val(i) and carry;
        end loop;
        return result;
    end function;

    -- Function to decrement binary
    function decrement(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable borrow : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := val(i) xor borrow;
            borrow := (not val(i)) and borrow;
        end loop;
        return result;
    end function;

    -- Function for binary to gray
    function bin2gray(bin : bit_vector) return bit_vector is
        variable gray : bit_vector(bin'range);
    begin
        gray(bin'high) := bin(bin'high);
        for i in bin'low to bin'high-1 loop
            gray(i) := bin(i) xor bin(i+1);
        end loop;
        return gray;
    end function;

begin
    counter_proc: process(clk, reset)
    begin
        if reset = '1' then
            binary_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            if enable = '1' then
                if count_up = '1' then
                    binary_reg <= increment(binary_reg);
                else
                    binary_reg <= decrement(binary_reg);
                end if;
            end if;
        end if;
    end process counter_proc;

    -- Convert to gray code
    gray_reg <= bin2gray(binary_reg);

    -- Outputs
    binary_count <= binary_reg;
    gray_count <= gray_reg;

end Behavioral;

-- ============================================================================
-- Dual-rail Gray Code Generator (for testing)
-- ============================================================================
entity GraySequenceGen is
    generic (
        WIDTH : integer := 4
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        start       : in  bit;
        gray_val    : out bit_vector(WIDTH-1 downto 0);
        binary_val  : out bit_vector(WIDTH-1 downto 0);
        sequence_done : out bit
    );
end GraySequenceGen;

architecture Behavioral of GraySequenceGen is
    type state_type is (IDLE, COUNTING, DONE);
    signal state : state_type;
    signal counter : bit_vector(WIDTH-1 downto 0);
    signal max_count : bit_vector(WIDTH-1 downto 0);

    -- Calculate 2^WIDTH - 1 (all ones)
    function all_ones(w : integer) return bit_vector is
        variable result : bit_vector(w-1 downto 0);
    begin
        for i in 0 to w-1 loop
            result(i) := '1';
        end loop;
        return result;
    end function;

    function increment(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable carry : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := val(i) xor carry;
            carry := val(i) and carry;
        end loop;
        return result;
    end function;

    function bin2gray(bin : bit_vector) return bit_vector is
        variable gray : bit_vector(bin'range);
    begin
        gray(bin'high) := bin(bin'high);
        for i in bin'low to bin'high-1 loop
            gray(i) := bin(i) xor bin(i+1);
        end loop;
        return gray;
    end function;

begin
    max_count <= all_ones(WIDTH);

    fsm_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE;
            counter <= (others => '0');
            sequence_done <= '0';
        elsif clk'event and clk = '1' then
            case state is
                when IDLE =>
                    sequence_done <= '0';
                    counter <= (others => '0');
                    if start = '1' then
                        state <= COUNTING;
                    end if;

                when COUNTING =>
                    counter <= increment(counter);
                    if counter = max_count then
                        state <= DONE;
                        sequence_done <= '1';
                    end if;

                when DONE =>
                    if start = '0' then
                        state <= IDLE;
                        sequence_done <= '0';
                    end if;
            end case;
        end if;
    end process fsm_proc;

    binary_val <= counter;
    gray_val <= bin2gray(counter);

end Behavioral;

-- ============================================================================
-- Johnson Counter (one-hot Gray-like sequence)
-- ============================================================================
entity JohnsonCounter is
    generic (
        WIDTH : integer := 8
    );
    port (
        clk     : in  bit;
        reset   : in  bit;
        enable  : in  bit;
        q       : out bit_vector(WIDTH-1 downto 0)
    );
end JohnsonCounter;

architecture Behavioral of JohnsonCounter is
    signal shift_reg : bit_vector(WIDTH-1 downto 0);
begin
    process(clk, reset)
    begin
        if reset = '1' then
            shift_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            if enable = '1' then
                -- Shift left and feedback inverted LSB to MSB
                shift_reg(WIDTH-1 downto 1) <= shift_reg(WIDTH-2 downto 0);
                shift_reg(0) <= not shift_reg(WIDTH-1);
            end if;
        end if;
    end process;

    q <= shift_reg;
end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity GrayCodec_TB is
end GrayCodec_TB;

architecture Test of GrayCodec_TB is
    constant WIDTH : integer := 4;
    signal tb_binary_in  : bit_vector(WIDTH-1 downto 0);
    signal tb_gray_in    : bit_vector(WIDTH-1 downto 0);
    signal tb_gray_out   : bit_vector(WIDTH-1 downto 0);
    signal tb_binary_out : bit_vector(WIDTH-1 downto 0);

    signal tb_clk, tb_reset, tb_enable : bit;
    signal tb_gray_count, tb_binary_count : bit_vector(WIDTH-1 downto 0);

    function increment(val : bit_vector) return bit_vector is
        variable result : bit_vector(val'range);
        variable carry : bit := '1';
    begin
        for i in val'low to val'high loop
            result(i) := val(i) xor carry;
            carry := val(i) and carry;
        end loop;
        return result;
    end function;

begin
    -- Test codec
    codec: entity work.GrayCodec
        generic map (WIDTH => WIDTH)
        port map (
            binary_in  => tb_binary_in,
            gray_in    => tb_gray_in,
            gray_out   => tb_gray_out,
            binary_out => tb_binary_out
        );

    -- Test counter
    counter: entity work.GrayCounter
        generic map (WIDTH => WIDTH)
        port map (
            clk          => tb_clk,
            reset        => tb_reset,
            enable       => tb_enable,
            count_up     => '1',
            gray_count   => tb_gray_count,
            binary_count => tb_binary_count
        );

    -- Clock generation
    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_enable <= '0';
        tb_binary_in <= "0000";
        tb_gray_in <= "0000";
        wait for 20 ns;

        tb_reset <= '0';
        tb_enable <= '1';

        -- Test all binary values
        for i in 0 to 15 loop
            wait for 10 ns;
            tb_binary_in <= increment(tb_binary_in);
            tb_gray_in <= tb_gray_out;  -- Feed back for verification
        end loop;

        wait;
    end process stimulus;
end Test;
