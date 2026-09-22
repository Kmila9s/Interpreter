-- ============================================================================
-- VHDL Project: CircularBuffer
-- Category: Memory Structures
-- Complexity: Medium
-- Description: Ring buffer for streaming data applications
--              DMA-style interface with wrap-around addressing
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity CircularBuffer is
    generic (
        DATA_WIDTH : integer := 8;
        ADDR_WIDTH : integer := 10  -- 1024 entries
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Producer interface
        write_en    : in  bit;
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Consumer interface
        read_en     : in  bit;
        read_data   : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Control
        clear       : in  bit;
        -- Status
        count       : out bit_vector(ADDR_WIDTH downto 0);
        empty       : out bit;
        full        : out bit;
        overflow    : out bit;
        underflow   : out bit;
        -- Debug
        write_ptr   : out bit_vector(ADDR_WIDTH-1 downto 0);
        read_ptr    : out bit_vector(ADDR_WIDTH-1 downto 0)
    );
end CircularBuffer;

architecture Behavioral of CircularBuffer is
    constant DEPTH : integer := 2 ** ADDR_WIDTH;
    type buffer_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal buffer_mem : buffer_type;

    signal wr_ptr : integer range 0 to DEPTH-1;
    signal rd_ptr : integer range 0 to DEPTH-1;
    signal word_count : integer range 0 to DEPTH;

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer := val;
    begin
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    buffer_proc: process(clk, reset)
        variable next_wr : integer;
        variable next_rd : integer;
    begin
        if reset = '1' then
            wr_ptr <= 0;
            rd_ptr <= 0;
            word_count <= 0;
            read_data <= (others => '0');
            overflow <= '0';
            underflow <= '0';
        elsif clk'event and clk = '1' then
            overflow <= '0';
            underflow <= '0';

            if clear = '1' then
                wr_ptr <= 0;
                rd_ptr <= 0;
                word_count <= 0;
            else
                -- Write
                if write_en = '1' then
                    if word_count < DEPTH then
                        buffer_mem(wr_ptr) <= write_data;
                        if wr_ptr = DEPTH - 1 then
                            wr_ptr <= 0;
                        else
                            wr_ptr <= wr_ptr + 1;
                        end if;
                    else
                        overflow <= '1';
                    end if;
                end if;

                -- Read
                if read_en = '1' then
                    if word_count > 0 then
                        read_data <= buffer_mem(rd_ptr);
                        if rd_ptr = DEPTH - 1 then
                            rd_ptr <= 0;
                        else
                            rd_ptr <= rd_ptr + 1;
                        end if;
                    else
                        underflow <= '1';
                    end if;
                end if;

                -- Update count
                if write_en = '1' and read_en = '0' then
                    if word_count < DEPTH then
                        word_count <= word_count + 1;
                    end if;
                elsif write_en = '0' and read_en = '1' then
                    if word_count > 0 then
                        word_count <= word_count - 1;
                    end if;
                end if;
            end if;
        end if;
    end process;

    count <= to_bv(word_count, ADDR_WIDTH + 1);
    empty <= '1' when word_count = 0 else '0';
    full <= '1' when word_count = DEPTH else '0';
    write_ptr <= to_bv(wr_ptr, ADDR_WIDTH);
    read_ptr <= to_bv(rd_ptr, ADDR_WIDTH);

end Behavioral;

-- ============================================================================
-- Ping-Pong Buffer (Double buffering)
-- ============================================================================
entity PingPongBuffer is
    generic (
        DATA_WIDTH : integer := 8;
        BUFFER_SIZE : integer := 256
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Write interface
        write_en    : in  bit;
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        write_done  : in  bit;  -- Finished writing current buffer
        -- Read interface
        read_en     : in  bit;
        read_addr   : in  bit_vector(7 downto 0);
        read_data   : out bit_vector(DATA_WIDTH-1 downto 0);
        read_done   : in  bit;  -- Finished reading current buffer
        -- Status
        write_buf   : out bit;  -- Which buffer is being written
        read_buf    : out bit;  -- Which buffer is being read
        buf_ready   : out bit   -- A complete buffer is ready for reading
    );
end PingPongBuffer;

architecture Behavioral of PingPongBuffer is
    type buffer_type is array (0 to BUFFER_SIZE-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal buffer_0, buffer_1 : buffer_type;

    signal wr_buf_sel : bit;  -- '0' = buffer_0, '1' = buffer_1
    signal rd_buf_sel : bit;
    signal wr_addr : integer range 0 to BUFFER_SIZE-1;
    signal buffer_ready : bit;

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
    begin
        if reset = '1' then
            wr_buf_sel <= '0';
            rd_buf_sel <= '1';  -- Start reading from buffer 1 (will swap)
            wr_addr <= 0;
            buffer_ready <= '0';
        elsif clk'event and clk = '1' then
            -- Write to current buffer
            if write_en = '1' then
                if wr_buf_sel = '0' then
                    buffer_0(wr_addr) <= write_data;
                else
                    buffer_1(wr_addr) <= write_data;
                end if;
                if wr_addr < BUFFER_SIZE - 1 then
                    wr_addr <= wr_addr + 1;
                end if;
            end if;

            -- Swap buffers when write completes
            if write_done = '1' then
                wr_buf_sel <= not wr_buf_sel;
                wr_addr <= 0;
                buffer_ready <= '1';
            end if;

            -- Swap read buffer when read completes
            if read_done = '1' then
                rd_buf_sel <= not rd_buf_sel;
                buffer_ready <= '0';
            end if;
        end if;
    end process;

    -- Read from appropriate buffer
    read_data <= buffer_0(to_int(read_addr)) when rd_buf_sel = '0' else
                 buffer_1(to_int(read_addr));

    write_buf <= wr_buf_sel;
    read_buf <= rd_buf_sel;
    buf_ready <= buffer_ready;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity CircularBuffer_TB is
end CircularBuffer_TB;

architecture Test of CircularBuffer_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_write_en, tb_read_en, tb_clear : bit;
    signal tb_write_data, tb_read_data : bit_vector(7 downto 0);
    signal tb_count : bit_vector(10 downto 0);
    signal tb_empty, tb_full, tb_overflow, tb_underflow : bit;
    signal tb_wr_ptr, tb_rd_ptr : bit_vector(9 downto 0);
begin
    UUT: entity work.CircularBuffer
        generic map (DATA_WIDTH => 8, ADDR_WIDTH => 10)
        port map (
            clk => tb_clk, reset => tb_reset,
            write_en => tb_write_en, write_data => tb_write_data,
            read_en => tb_read_en, read_data => tb_read_data,
            clear => tb_clear, count => tb_count,
            empty => tb_empty, full => tb_full,
            overflow => tb_overflow, underflow => tb_underflow,
            write_ptr => tb_wr_ptr, read_ptr => tb_rd_ptr
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_write_en <= '0'; tb_read_en <= '0'; tb_clear <= '0';
        tb_write_data <= (others => '0');
        wait for 30 ns;

        tb_reset <= '0';

        -- Write data
        tb_write_en <= '1';
        for i in 0 to 7 loop
            tb_write_data <= "00001111";
            wait for 10 ns;
        end loop;
        tb_write_en <= '0';

        -- Read data
        tb_read_en <= '1';
        wait for 80 ns;
        tb_read_en <= '0';

        wait for 50 ns;
        wait;
    end process;
end Test;
