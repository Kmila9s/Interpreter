-- ============================================================================
-- VHDL Project: SyncFIFO
-- Category: Memory Structures
-- Complexity: Medium
-- Description: Synchronous FIFO with configurable depth
--              Includes full, empty, almost full/empty flags
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity SyncFIFO is
    generic (
        DATA_WIDTH  : integer := 8;
        DEPTH       : integer := 16;
        ALMOST_FULL : integer := 14;
        ALMOST_EMPTY: integer := 2
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Write interface
        write_en    : in  bit;
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Read interface
        read_en     : in  bit;
        read_data   : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Status flags
        full        : out bit;
        empty       : out bit;
        almost_full : out bit;
        almost_empty: out bit;
        count       : out bit_vector(7 downto 0)
    );
end SyncFIFO;

architecture Behavioral of SyncFIFO is
    type fifo_mem_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal fifo_mem : fifo_mem_type;

    signal write_ptr : integer range 0 to DEPTH-1;
    signal read_ptr : integer range 0 to DEPTH-1;
    signal word_count : integer range 0 to DEPTH;

    signal full_flag : bit;
    signal empty_flag : bit;

    function to_bv8(val : integer) return bit_vector is
        variable result : bit_vector(7 downto 0);
        variable temp : integer := val;
    begin
        for i in 0 to 7 loop
            if (temp mod 2) = 1 then
                result(i) := '1';
            else
                result(i) := '0';
            end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    full_flag <= '1' when word_count = DEPTH else '0';
    empty_flag <= '1' when word_count = 0 else '0';

    fifo_proc: process(clk, reset)
    begin
        if reset = '1' then
            write_ptr <= 0;
            read_ptr <= 0;
            word_count <= 0;
            read_data <= (others => '0');
        elsif clk'event and clk = '1' then
            -- Write operation
            if write_en = '1' and full_flag = '0' then
                fifo_mem(write_ptr) <= write_data;
                if write_ptr = DEPTH - 1 then
                    write_ptr <= 0;
                else
                    write_ptr <= write_ptr + 1;
                end if;
            end if;

            -- Read operation
            if read_en = '1' and empty_flag = '0' then
                read_data <= fifo_mem(read_ptr);
                if read_ptr = DEPTH - 1 then
                    read_ptr <= 0;
                else
                    read_ptr <= read_ptr + 1;
                end if;
            end if;

            -- Update count
            if write_en = '1' and read_en = '0' and full_flag = '0' then
                word_count <= word_count + 1;
            elsif write_en = '0' and read_en = '1' and empty_flag = '0' then
                word_count <= word_count - 1;
            end if;
        end if;
    end process;

    full <= full_flag;
    empty <= empty_flag;
    almost_full <= '1' when word_count >= ALMOST_FULL else '0';
    almost_empty <= '1' when word_count <= ALMOST_EMPTY else '0';
    count <= to_bv8(word_count);

end Behavioral;

-- ============================================================================
-- FWFT (First Word Fall Through) FIFO
-- ============================================================================
entity FWFTFifo is
    generic (
        DATA_WIDTH : integer := 8;
        DEPTH      : integer := 16
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        write_en    : in  bit;
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        read_en     : in  bit;
        read_data   : out bit_vector(DATA_WIDTH-1 downto 0);
        valid       : out bit;  -- Data valid on read_data
        full        : out bit;
        empty       : out bit
    );
end FWFTFifo;

architecture Behavioral of FWFTFifo is
    type fifo_mem_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal fifo_mem : fifo_mem_type;

    signal wr_ptr, rd_ptr : integer range 0 to DEPTH-1;
    signal count : integer range 0 to DEPTH;

    signal full_i, empty_i : bit;
    signal output_reg : bit_vector(DATA_WIDTH-1 downto 0);
    signal output_valid : bit;

begin
    full_i <= '1' when count = DEPTH else '0';
    empty_i <= '1' when count = 0 else '0';

    process(clk, reset)
    begin
        if reset = '1' then
            wr_ptr <= 0;
            rd_ptr <= 0;
            count <= 0;
            output_reg <= (others => '0');
            output_valid <= '0';
        elsif clk'event and clk = '1' then
            -- Write
            if write_en = '1' and full_i = '0' then
                fifo_mem(wr_ptr) <= write_data;
                if wr_ptr = DEPTH - 1 then
                    wr_ptr <= 0;
                else
                    wr_ptr <= wr_ptr + 1;
                end if;
            end if;

            -- FWFT: Always present first word if available
            if count > 0 or (write_en = '1' and count = 0) then
                if count = 0 and write_en = '1' then
                    -- Bypass: new data goes directly to output
                    output_reg <= write_data;
                    output_valid <= '1';
                elsif read_en = '1' then
                    -- Advance to next word
                    if count > 1 then
                        output_reg <= fifo_mem(rd_ptr);
                        output_valid <= '1';
                    else
                        output_valid <= '0';
                    end if;
                else
                    output_reg <= fifo_mem(rd_ptr);
                    output_valid <= '1';
                end if;
            else
                output_valid <= '0';
            end if;

            -- Update read pointer on read
            if read_en = '1' and empty_i = '0' then
                if rd_ptr = DEPTH - 1 then
                    rd_ptr <= 0;
                else
                    rd_ptr <= rd_ptr + 1;
                end if;
            end if;

            -- Update count
            if write_en = '1' and read_en = '0' and full_i = '0' then
                count <= count + 1;
            elsif write_en = '0' and read_en = '1' and empty_i = '0' then
                count <= count - 1;
            end if;
        end if;
    end process;

    read_data <= output_reg;
    valid <= output_valid;
    full <= full_i;
    empty <= empty_i;

end Behavioral;

-- ============================================================================
-- Packet FIFO (Stores complete packets)
-- ============================================================================
entity PacketFIFO is
    generic (
        DATA_WIDTH  : integer := 8;
        DEPTH       : integer := 256;
        MAX_PACKETS : integer := 8
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Write interface
        write_en    : in  bit;
        write_data  : in  bit_vector(DATA_WIDTH-1 downto 0);
        write_sop   : in  bit;  -- Start of packet
        write_eop   : in  bit;  -- End of packet
        -- Read interface
        read_en     : in  bit;
        read_data   : out bit_vector(DATA_WIDTH-1 downto 0);
        read_sop    : out bit;
        read_eop    : out bit;
        -- Status
        packet_ready: out bit;  -- Complete packet available
        full        : out bit;
        empty       : out bit
    );
end PacketFIFO;

architecture Behavioral of PacketFIFO is
    type data_mem_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    type flag_mem_type is array (0 to DEPTH-1) of bit_vector(1 downto 0);

    signal data_mem : data_mem_type;
    signal flag_mem : flag_mem_type;  -- (0)=SOP, (1)=EOP

    signal wr_ptr, rd_ptr : integer range 0 to DEPTH-1;
    signal count : integer range 0 to DEPTH;
    signal packet_count : integer range 0 to MAX_PACKETS;
    signal in_packet : bit;

begin
    process(clk, reset)
        variable flags : bit_vector(1 downto 0);
    begin
        if reset = '1' then
            wr_ptr <= 0;
            rd_ptr <= 0;
            count <= 0;
            packet_count <= 0;
            in_packet <= '0';
            read_data <= (others => '0');
            read_sop <= '0';
            read_eop <= '0';
        elsif clk'event and clk = '1' then
            read_sop <= '0';
            read_eop <= '0';

            -- Write
            if write_en = '1' and count < DEPTH then
                data_mem(wr_ptr) <= write_data;
                flag_mem(wr_ptr) <= write_eop & write_sop;

                if write_sop = '1' then
                    in_packet <= '1';
                end if;

                if write_eop = '1' then
                    in_packet <= '0';
                    if packet_count < MAX_PACKETS then
                        packet_count <= packet_count + 1;
                    end if;
                end if;

                if wr_ptr = DEPTH - 1 then
                    wr_ptr <= 0;
                else
                    wr_ptr <= wr_ptr + 1;
                end if;
                count <= count + 1;
            end if;

            -- Read
            if read_en = '1' and count > 0 then
                read_data <= data_mem(rd_ptr);
                flags := flag_mem(rd_ptr);
                read_sop <= flags(0);
                read_eop <= flags(1);

                if flags(1) = '1' and packet_count > 0 then
                    packet_count <= packet_count - 1;
                end if;

                if rd_ptr = DEPTH - 1 then
                    rd_ptr <= 0;
                else
                    rd_ptr <= rd_ptr + 1;
                end if;
                count <= count - 1;
            end if;
        end if;
    end process;

    packet_ready <= '1' when packet_count > 0 else '0';
    full <= '1' when count = DEPTH else '0';
    empty <= '1' when count = 0 else '0';

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity SyncFIFO_TB is
end SyncFIFO_TB;

architecture Test of SyncFIFO_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_write_en : bit;
    signal tb_write_data : bit_vector(7 downto 0);
    signal tb_read_en : bit;
    signal tb_read_data : bit_vector(7 downto 0);
    signal tb_full, tb_empty : bit;
    signal tb_almost_full, tb_almost_empty : bit;
    signal tb_count : bit_vector(7 downto 0);
begin
    UUT: entity work.SyncFIFO
        generic map (DATA_WIDTH => 8, DEPTH => 16, ALMOST_FULL => 14, ALMOST_EMPTY => 2)
        port map (
            clk          => tb_clk,
            reset        => tb_reset,
            write_en     => tb_write_en,
            write_data   => tb_write_data,
            read_en      => tb_read_en,
            read_data    => tb_read_data,
            full         => tb_full,
            empty        => tb_empty,
            almost_full  => tb_almost_full,
            almost_empty => tb_almost_empty,
            count        => tb_count
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_write_en <= '0';
        tb_read_en <= '0';
        tb_write_data <= (others => '0');
        wait for 30 ns;

        tb_reset <= '0';

        -- Write some data
        for i in 0 to 7 loop
            tb_write_en <= '1';
            tb_write_data <= "0000" & "0000";
            wait for 10 ns;
        end loop;
        tb_write_en <= '0';

        wait for 20 ns;

        -- Read data
        for i in 0 to 3 loop
            tb_read_en <= '1';
            wait for 10 ns;
        end loop;
        tb_read_en <= '0';

        wait for 50 ns;
        wait;
    end process;
end Test;
