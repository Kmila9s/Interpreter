-- ============================================================================
-- VHDL Project: CacheController
-- Category: Memory Structures
-- Complexity: High
-- Description: Direct-mapped cache controller with write-back policy
--              Handles cache hits, misses, and memory transactions
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity CacheController is
    generic (
        DATA_WIDTH   : integer := 32;
        ADDR_WIDTH   : integer := 32;
        CACHE_SIZE   : integer := 1024;  -- Bytes
        LINE_SIZE    : integer := 16;     -- Bytes per cache line
        TAG_WIDTH    : integer := 20
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- CPU interface
        cpu_addr        : in  bit_vector(ADDR_WIDTH-1 downto 0);
        cpu_data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        cpu_data_out    : out bit_vector(DATA_WIDTH-1 downto 0);
        cpu_read        : in  bit;
        cpu_write       : in  bit;
        cpu_ready       : out bit;
        -- Memory interface
        mem_addr        : out bit_vector(ADDR_WIDTH-1 downto 0);
        mem_data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        mem_data_out    : out bit_vector(DATA_WIDTH-1 downto 0);
        mem_read        : out bit;
        mem_write       : out bit;
        mem_ready       : in  bit;
        -- Status
        cache_hit       : out bit;
        cache_miss      : out bit;
        state_out       : out bit_vector(3 downto 0)
    );
end CacheController;

architecture Behavioral of CacheController is
    -- Cache parameters
    constant NUM_LINES : integer := CACHE_SIZE / LINE_SIZE;  -- 64 lines
    constant WORDS_PER_LINE : integer := LINE_SIZE / (DATA_WIDTH / 8);  -- 4 words
    constant INDEX_BITS : integer := 6;  -- log2(NUM_LINES)
    constant OFFSET_BITS : integer := 4; -- log2(LINE_SIZE)
    constant WORD_OFFSET_BITS : integer := 2; -- log2(WORDS_PER_LINE)

    -- Cache line structure
    type cache_data_type is array (0 to WORDS_PER_LINE-1) of bit_vector(DATA_WIDTH-1 downto 0);
    type cache_line_type is record
        valid : bit;
        dirty : bit;
        tag   : bit_vector(TAG_WIDTH-1 downto 0);
        data  : cache_data_type;
    end record;

    type cache_type is array (0 to NUM_LINES-1) of cache_line_type;
    signal cache : cache_type;

    -- FSM states
    type state_type is (
        IDLE,
        COMPARE_TAG,
        WRITE_BACK_INIT,
        WRITE_BACK,
        ALLOCATE_INIT,
        ALLOCATE,
        UPDATE_CACHE
    );
    signal state : state_type;

    -- Address breakdown
    signal addr_tag : bit_vector(TAG_WIDTH-1 downto 0);
    signal addr_index : integer range 0 to NUM_LINES-1;
    signal addr_word_offset : integer range 0 to WORDS_PER_LINE-1;

    -- Transfer counters
    signal word_counter : integer range 0 to WORDS_PER_LINE;

    -- Latched request
    signal req_addr : bit_vector(ADDR_WIDTH-1 downto 0);
    signal req_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal req_write : bit;

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

    function tags_match(a, b : bit_vector) return bit is
    begin
        for i in a'range loop
            if a(i) /= b(i) then
                return '0';
            end if;
        end loop;
        return '1';
    end function;

begin
    -- Address decoding
    addr_tag <= cpu_addr(ADDR_WIDTH-1 downto ADDR_WIDTH-TAG_WIDTH);
    addr_index <= to_integer(cpu_addr(INDEX_BITS+OFFSET_BITS-1 downto OFFSET_BITS));
    addr_word_offset <= to_integer(cpu_addr(WORD_OFFSET_BITS+1 downto 2));

    -- Main FSM
    cache_fsm: process(clk, reset)
        variable line : cache_line_type;
        variable hit : bit;
        variable wb_addr : bit_vector(ADDR_WIDTH-1 downto 0);
    begin
        if reset = '1' then
            state <= IDLE;
            word_counter <= 0;
            cpu_ready <= '1';
            cache_hit <= '0';
            cache_miss <= '0';
            mem_read <= '0';
            mem_write <= '0';
            mem_addr <= (others => '0');
            mem_data_out <= (others => '0');
            cpu_data_out <= (others => '0');
            -- Invalidate all cache lines
            for i in 0 to NUM_LINES-1 loop
                cache(i).valid <= '0';
                cache(i).dirty <= '0';
            end loop;
        elsif clk'event and clk = '1' then
            cache_hit <= '0';
            cache_miss <= '0';

            case state is
                when IDLE =>
                    cpu_ready <= '1';
                    if cpu_read = '1' or cpu_write = '1' then
                        req_addr <= cpu_addr;
                        req_data <= cpu_data_in;
                        req_write <= cpu_write;
                        cpu_ready <= '0';
                        state <= COMPARE_TAG;
                    end if;

                when COMPARE_TAG =>
                    line := cache(addr_index);
                    hit := line.valid and tags_match(line.tag, addr_tag);

                    if hit = '1' then
                        cache_hit <= '1';
                        if req_write = '1' then
                            -- Write hit: update cache
                            cache(addr_index).data(addr_word_offset) <= req_data;
                            cache(addr_index).dirty <= '1';
                        else
                            -- Read hit: return data
                            cpu_data_out <= line.data(addr_word_offset);
                        end if;
                        state <= IDLE;
                    else
                        cache_miss <= '1';
                        -- Cache miss
                        if line.valid = '1' and line.dirty = '1' then
                            -- Need to write back dirty line first
                            state <= WRITE_BACK_INIT;
                        else
                            -- Can allocate directly
                            state <= ALLOCATE_INIT;
                        end if;
                    end if;

                when WRITE_BACK_INIT =>
                    -- Start write-back
                    line := cache(addr_index);
                    -- Construct write-back address
                    wb_addr := line.tag & to_bv(addr_index, INDEX_BITS) & to_bv(0, OFFSET_BITS);
                    mem_addr <= wb_addr;
                    mem_data_out <= line.data(0);
                    mem_write <= '1';
                    word_counter <= 0;
                    state <= WRITE_BACK;

                when WRITE_BACK =>
                    if mem_ready = '1' then
                        word_counter <= word_counter + 1;
                        if word_counter >= WORDS_PER_LINE - 1 then
                            mem_write <= '0';
                            state <= ALLOCATE_INIT;
                        else
                            mem_data_out <= cache(addr_index).data(word_counter + 1);
                            mem_addr(WORD_OFFSET_BITS+1 downto 2) <= to_bv(word_counter + 1, WORD_OFFSET_BITS);
                        end if;
                    end if;

                when ALLOCATE_INIT =>
                    -- Start fetching new line
                    mem_addr <= req_addr(ADDR_WIDTH-1 downto OFFSET_BITS) & to_bv(0, OFFSET_BITS);
                    mem_read <= '1';
                    word_counter <= 0;
                    state <= ALLOCATE;

                when ALLOCATE =>
                    if mem_ready = '1' then
                        cache(addr_index).data(word_counter) <= mem_data_in;
                        word_counter <= word_counter + 1;

                        if word_counter >= WORDS_PER_LINE - 1 then
                            mem_read <= '0';
                            state <= UPDATE_CACHE;
                        else
                            mem_addr(WORD_OFFSET_BITS+1 downto 2) <= to_bv(word_counter + 1, WORD_OFFSET_BITS);
                        end if;
                    end if;

                when UPDATE_CACHE =>
                    -- Update cache metadata
                    cache(addr_index).valid <= '1';
                    cache(addr_index).tag <= addr_tag;

                    if req_write = '1' then
                        -- Write the new data
                        cache(addr_index).data(addr_word_offset) <= req_data;
                        cache(addr_index).dirty <= '1';
                    else
                        -- Return the fetched data
                        cpu_data_out <= cache(addr_index).data(addr_word_offset);
                        cache(addr_index).dirty <= '0';
                    end if;
                    state <= IDLE;

            end case;
        end if;
    end process;

    -- State output
    with state select state_out <=
        "0000" when IDLE,
        "0001" when COMPARE_TAG,
        "0010" when WRITE_BACK_INIT,
        "0011" when WRITE_BACK,
        "0100" when ALLOCATE_INIT,
        "0101" when ALLOCATE,
        "0110" when UPDATE_CACHE;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity CacheController_TB is
end CacheController_TB;

architecture Test of CacheController_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_cpu_addr : bit_vector(31 downto 0);
    signal tb_cpu_data_in, tb_cpu_data_out : bit_vector(31 downto 0);
    signal tb_cpu_read, tb_cpu_write, tb_cpu_ready : bit;
    signal tb_mem_addr : bit_vector(31 downto 0);
    signal tb_mem_data_in, tb_mem_data_out : bit_vector(31 downto 0);
    signal tb_mem_read, tb_mem_write, tb_mem_ready : bit;
    signal tb_cache_hit, tb_cache_miss : bit;
    signal tb_state : bit_vector(3 downto 0);
begin
    UUT: entity work.CacheController
        port map (
            clk          => tb_clk,
            reset        => tb_reset,
            cpu_addr     => tb_cpu_addr,
            cpu_data_in  => tb_cpu_data_in,
            cpu_data_out => tb_cpu_data_out,
            cpu_read     => tb_cpu_read,
            cpu_write    => tb_cpu_write,
            cpu_ready    => tb_cpu_ready,
            mem_addr     => tb_mem_addr,
            mem_data_in  => tb_mem_data_in,
            mem_data_out => tb_mem_data_out,
            mem_read     => tb_mem_read,
            mem_write    => tb_mem_write,
            mem_ready    => tb_mem_ready,
            cache_hit    => tb_cache_hit,
            cache_miss   => tb_cache_miss,
            state_out    => tb_state
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    -- Simple memory model
    mem_model: process(tb_clk)
    begin
        if tb_clk'event and tb_clk = '1' then
            if tb_mem_read = '1' or tb_mem_write = '1' then
                tb_mem_ready <= '1';
                tb_mem_data_in <= "10101010101010101010101010101010";  -- Dummy data
            else
                tb_mem_ready <= '0';
            end if;
        end if;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_cpu_addr <= (others => '0');
        tb_cpu_data_in <= (others => '0');
        tb_cpu_read <= '0';
        tb_cpu_write <= '0';
        wait for 50 ns;

        tb_reset <= '0';
        wait for 20 ns;

        -- Read miss
        tb_cpu_addr <= "00000000000000000000000100000000";
        tb_cpu_read <= '1';
        wait for 10 ns;
        tb_cpu_read <= '0';
        wait until tb_cpu_ready = '1';
        wait for 20 ns;

        -- Read hit (same line)
        tb_cpu_addr <= "00000000000000000000000100000100";
        tb_cpu_read <= '1';
        wait for 10 ns;
        tb_cpu_read <= '0';
        wait until tb_cpu_ready = '1';
        wait for 20 ns;

        -- Write hit
        tb_cpu_addr <= "00000000000000000000000100000000";
        tb_cpu_data_in <= "11111111000000001111111100000000";
        tb_cpu_write <= '1';
        wait for 10 ns;
        tb_cpu_write <= '0';
        wait until tb_cpu_ready = '1';

        wait for 100 ns;
        wait;
    end process;
end Test;
