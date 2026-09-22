-- ============================================================================
-- VHDL Project: AsyncFIFO
-- Category: Memory Structures
-- Complexity: High
-- Description: Asynchronous FIFO for clock domain crossing
--              Uses Gray code pointers for safe synchronization
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity AsyncFIFO is
    generic (
        DATA_WIDTH : integer := 8;
        ADDR_WIDTH : integer := 4   -- Depth = 2^ADDR_WIDTH
    );
    port (
        -- Write domain
        wr_clk      : in  bit;
        wr_reset    : in  bit;
        wr_en       : in  bit;
        wr_data     : in  bit_vector(DATA_WIDTH-1 downto 0);
        wr_full     : out bit;
        -- Read domain
        rd_clk      : in  bit;
        rd_reset    : in  bit;
        rd_en       : in  bit;
        rd_data     : out bit_vector(DATA_WIDTH-1 downto 0);
        rd_empty    : out bit
    );
end AsyncFIFO;

architecture Behavioral of AsyncFIFO is
    constant DEPTH : integer := 2 ** ADDR_WIDTH;

    type fifo_mem_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal fifo_mem : fifo_mem_type;

    -- Binary pointers
    signal wr_ptr_bin : bit_vector(ADDR_WIDTH downto 0);  -- Extra bit for wrap
    signal rd_ptr_bin : bit_vector(ADDR_WIDTH downto 0);

    -- Gray code pointers
    signal wr_ptr_gray : bit_vector(ADDR_WIDTH downto 0);
    signal rd_ptr_gray : bit_vector(ADDR_WIDTH downto 0);

    -- Synchronized pointers
    signal wr_ptr_gray_sync1, wr_ptr_gray_sync2 : bit_vector(ADDR_WIDTH downto 0);
    signal rd_ptr_gray_sync1, rd_ptr_gray_sync2 : bit_vector(ADDR_WIDTH downto 0);

    -- Status signals
    signal full_flag : bit;
    signal empty_flag : bit;

    -- Binary to Gray conversion
    function bin_to_gray(bin : bit_vector) return bit_vector is
        variable gray : bit_vector(bin'range);
    begin
        gray(bin'high) := bin(bin'high);
        for i in bin'high-1 downto bin'low loop
            gray(i) := bin(i+1) xor bin(i);
        end loop;
        return gray;
    end function;

    -- Gray to Binary conversion
    function gray_to_bin(gray : bit_vector) return bit_vector is
        variable bin : bit_vector(gray'range);
    begin
        bin(gray'high) := gray(gray'high);
        for i in gray'high-1 downto gray'low loop
            bin(i) := bin(i+1) xor gray(i);
        end loop;
        return bin;
    end function;

    -- Increment binary pointer
    function increment(ptr : bit_vector) return bit_vector is
        variable result : bit_vector(ptr'range);
        variable carry : bit := '1';
    begin
        for i in ptr'low to ptr'high loop
            result(i) := ptr(i) xor carry;
            carry := ptr(i) and carry;
        end loop;
        return result;
    end function;

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

    function vectors_equal(a, b : bit_vector) return bit is
    begin
        for i in a'range loop
            if a(i) /= b(i) then
                return '0';
            end if;
        end loop;
        return '1';
    end function;

begin
    -- ========================================
    -- Write Domain Logic
    -- ========================================

    -- Synchronize read pointer to write domain
    wr_sync_proc: process(wr_clk, wr_reset)
    begin
        if wr_reset = '1' then
            rd_ptr_gray_sync1 <= (others => '0');
            rd_ptr_gray_sync2 <= (others => '0');
        elsif wr_clk'event and wr_clk = '1' then
            rd_ptr_gray_sync1 <= rd_ptr_gray;
            rd_ptr_gray_sync2 <= rd_ptr_gray_sync1;
        end if;
    end process;

    -- Write logic
    wr_proc: process(wr_clk, wr_reset)
        variable wr_addr : integer;
        variable next_wr_ptr : bit_vector(ADDR_WIDTH downto 0);
    begin
        if wr_reset = '1' then
            wr_ptr_bin <= (others => '0');
            wr_ptr_gray <= (others => '0');
        elsif wr_clk'event and wr_clk = '1' then
            if wr_en = '1' and full_flag = '0' then
                -- Write data
                wr_addr := to_integer(wr_ptr_bin(ADDR_WIDTH-1 downto 0));
                fifo_mem(wr_addr) <= wr_data;

                -- Increment pointer
                next_wr_ptr := increment(wr_ptr_bin);
                wr_ptr_bin <= next_wr_ptr;
                wr_ptr_gray <= bin_to_gray(next_wr_ptr);
            end if;
        end if;
    end process;

    -- Full detection (in write domain)
    -- FIFO is full when write pointer's Gray code equals read pointer's
    -- Gray code with MSB inverted and second MSB inverted
    full_proc: process(wr_ptr_gray, rd_ptr_gray_sync2)
        variable full_check : bit_vector(ADDR_WIDTH downto 0);
    begin
        full_check := rd_ptr_gray_sync2;
        full_check(ADDR_WIDTH) := not full_check(ADDR_WIDTH);
        full_check(ADDR_WIDTH-1) := not full_check(ADDR_WIDTH-1);

        full_flag <= vectors_equal(wr_ptr_gray, full_check);
    end process;

    -- ========================================
    -- Read Domain Logic
    -- ========================================

    -- Synchronize write pointer to read domain
    rd_sync_proc: process(rd_clk, rd_reset)
    begin
        if rd_reset = '1' then
            wr_ptr_gray_sync1 <= (others => '0');
            wr_ptr_gray_sync2 <= (others => '0');
        elsif rd_clk'event and rd_clk = '1' then
            wr_ptr_gray_sync1 <= wr_ptr_gray;
            wr_ptr_gray_sync2 <= wr_ptr_gray_sync1;
        end if;
    end process;

    -- Read logic
    rd_proc: process(rd_clk, rd_reset)
        variable rd_addr : integer;
        variable next_rd_ptr : bit_vector(ADDR_WIDTH downto 0);
    begin
        if rd_reset = '1' then
            rd_ptr_bin <= (others => '0');
            rd_ptr_gray <= (others => '0');
            rd_data <= (others => '0');
        elsif rd_clk'event and rd_clk = '1' then
            if rd_en = '1' and empty_flag = '0' then
                -- Read data
                rd_addr := to_integer(rd_ptr_bin(ADDR_WIDTH-1 downto 0));
                rd_data <= fifo_mem(rd_addr);

                -- Increment pointer
                next_rd_ptr := increment(rd_ptr_bin);
                rd_ptr_bin <= next_rd_ptr;
                rd_ptr_gray <= bin_to_gray(next_rd_ptr);
            end if;
        end if;
    end process;

    -- Empty detection (in read domain)
    -- FIFO is empty when read pointer equals write pointer
    empty_flag <= vectors_equal(rd_ptr_gray, wr_ptr_gray_sync2);

    -- Output assignments
    wr_full <= full_flag;
    rd_empty <= empty_flag;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity AsyncFIFO_TB is
end AsyncFIFO_TB;

architecture Test of AsyncFIFO_TB is
    signal tb_wr_clk, tb_wr_reset : bit;
    signal tb_rd_clk, tb_rd_reset : bit;
    signal tb_wr_en : bit;
    signal tb_wr_data : bit_vector(7 downto 0);
    signal tb_wr_full : bit;
    signal tb_rd_en : bit;
    signal tb_rd_data : bit_vector(7 downto 0);
    signal tb_rd_empty : bit;
begin
    UUT: entity work.AsyncFIFO
        generic map (DATA_WIDTH => 8, ADDR_WIDTH => 4)
        port map (
            wr_clk   => tb_wr_clk,
            wr_reset => tb_wr_reset,
            wr_en    => tb_wr_en,
            wr_data  => tb_wr_data,
            wr_full  => tb_wr_full,
            rd_clk   => tb_rd_clk,
            rd_reset => tb_rd_reset,
            rd_en    => tb_rd_en,
            rd_data  => tb_rd_data,
            rd_empty => tb_rd_empty
        );

    -- Write clock: 100 MHz
    wr_clk_gen: process
    begin
        tb_wr_clk <= '0'; wait for 5 ns;
        tb_wr_clk <= '1'; wait for 5 ns;
    end process;

    -- Read clock: 75 MHz
    rd_clk_gen: process
    begin
        tb_rd_clk <= '0'; wait for 6 ns;
        tb_rd_clk <= '1'; wait for 6 ns;
    end process;

    stimulus: process
    begin
        tb_wr_reset <= '1';
        tb_rd_reset <= '1';
        tb_wr_en <= '0';
        tb_rd_en <= '0';
        tb_wr_data <= (others => '0');
        wait for 50 ns;

        tb_wr_reset <= '0';
        tb_rd_reset <= '0';
        wait for 20 ns;

        -- Write data
        for i in 0 to 7 loop
            tb_wr_en <= '1';
            tb_wr_data <= "0000" & "0000";
            wait for 10 ns;
        end loop;
        tb_wr_en <= '0';

        wait for 50 ns;

        -- Read data
        for i in 0 to 7 loop
            tb_rd_en <= '1';
            wait for 12 ns;
        end loop;
        tb_rd_en <= '0';

        wait for 100 ns;
        wait;
    end process;
end Test;
