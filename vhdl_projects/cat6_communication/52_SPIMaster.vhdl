-- ============================================================================
-- VHDL Project: SPIMaster
-- Category: Communication Protocols
-- Complexity: Medium
-- Description: SPI Master controller with multiple modes
--              Configurable clock polarity/phase, variable bit width
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity SPIMaster is
    generic (
        CLK_DIV     : integer := 4;   -- Clock divider
        DATA_WIDTH  : integer := 8
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Control
        start       : in  bit;
        cpol        : in  bit;  -- Clock polarity
        cpha        : in  bit;  -- Clock phase
        -- Data
        data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        data_out    : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Status
        busy        : out bit;
        done        : out bit;
        -- SPI interface
        sclk        : out bit;
        mosi        : out bit;
        miso        : in  bit;
        cs_n        : out bit
    );
end SPIMaster;

architecture Behavioral of SPIMaster is
    type state_type is (IDLE, TRANSFER, DONE_STATE);
    signal state : state_type;

    signal clk_counter : integer range 0 to CLK_DIV-1;
    signal bit_counter : integer range 0 to DATA_WIDTH-1;
    signal sclk_reg : bit;
    signal shift_reg_out : bit_vector(DATA_WIDTH-1 downto 0);
    signal shift_reg_in : bit_vector(DATA_WIDTH-1 downto 0);
    signal sample_edge : bit;
    signal shift_edge : bit;

begin
    spi_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE;
            clk_counter <= 0;
            bit_counter <= 0;
            sclk_reg <= '0';
            cs_n <= '1';
            busy <= '0';
            done <= '0';
            shift_reg_out <= (others => '0');
            shift_reg_in <= (others => '0');
        elsif clk'event and clk = '1' then
            done <= '0';
            sample_edge <= '0';
            shift_edge <= '0';

            case state is
                when IDLE =>
                    cs_n <= '1';
                    sclk_reg <= cpol;  -- Idle clock state
                    busy <= '0';
                    clk_counter <= 0;
                    bit_counter <= DATA_WIDTH - 1;

                    if start = '1' then
                        shift_reg_out <= data_in;
                        shift_reg_in <= (others => '0');
                        cs_n <= '0';
                        busy <= '1';
                        state <= TRANSFER;

                        -- CPHA=0: output first bit immediately
                        if cpha = '0' then
                            mosi <= data_in(DATA_WIDTH - 1);
                        end if;
                    end if;

                when TRANSFER =>
                    -- Generate SPI clock
                    if clk_counter < CLK_DIV - 1 then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        sclk_reg <= not sclk_reg;

                        -- Determine sample and shift edges based on CPHA
                        if cpha = '0' then
                            -- CPHA=0: sample on leading edge, shift on trailing
                            if (cpol = '0' and sclk_reg = '0') or
                               (cpol = '1' and sclk_reg = '1') then
                                sample_edge <= '1';
                            else
                                shift_edge <= '1';
                            end if;
                        else
                            -- CPHA=1: shift on leading edge, sample on trailing
                            if (cpol = '0' and sclk_reg = '0') or
                               (cpol = '1' and sclk_reg = '1') then
                                shift_edge <= '1';
                            else
                                sample_edge <= '1';
                            end if;
                        end if;
                    end if;

                    -- Sample MISO
                    if sample_edge = '1' then
                        shift_reg_in <= shift_reg_in(DATA_WIDTH-2 downto 0) & miso;
                    end if;

                    -- Shift out next bit
                    if shift_edge = '1' then
                        if bit_counter > 0 then
                            bit_counter <= bit_counter - 1;
                            mosi <= shift_reg_out(bit_counter - 1);
                        else
                            state <= DONE_STATE;
                        end if;
                    end if;

                when DONE_STATE =>
                    -- Wait for final clock edge
                    if clk_counter < CLK_DIV - 1 then
                        clk_counter <= clk_counter + 1;
                    else
                        sclk_reg <= cpol;
                        cs_n <= '1';
                        data_out <= shift_reg_in;
                        done <= '1';
                        state <= IDLE;
                    end if;
            end case;
        end if;
    end process;

    sclk <= sclk_reg;

end Behavioral;

-- ============================================================================
-- SPI Slave
-- ============================================================================
entity SPISlave is
    generic (
        DATA_WIDTH : integer := 8
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Data interface
        tx_data     : in  bit_vector(DATA_WIDTH-1 downto 0);
        tx_load     : in  bit;
        rx_data     : out bit_vector(DATA_WIDTH-1 downto 0);
        rx_valid    : out bit;
        -- SPI interface
        sclk        : in  bit;
        mosi        : in  bit;
        miso        : out bit;
        cs_n        : in  bit
    );
end SPISlave;

architecture Behavioral of SPISlave is
    signal shift_reg_rx : bit_vector(DATA_WIDTH-1 downto 0);
    signal shift_reg_tx : bit_vector(DATA_WIDTH-1 downto 0);
    signal bit_counter : integer range 0 to DATA_WIDTH;
    signal sclk_prev : bit;
    signal cs_n_prev : bit;
begin
    process(clk, reset)
    begin
        if reset = '1' then
            shift_reg_rx <= (others => '0');
            shift_reg_tx <= (others => '0');
            bit_counter <= 0;
            sclk_prev <= '0';
            cs_n_prev <= '1';
            rx_valid <= '0';
            miso <= '0';
        elsif clk'event and clk = '1' then
            sclk_prev <= sclk;
            cs_n_prev <= cs_n;
            rx_valid <= '0';

            -- Load TX data
            if tx_load = '1' then
                shift_reg_tx <= tx_data;
            end if;

            if cs_n = '0' then
                -- Rising edge of SCLK - sample MOSI
                if sclk = '1' and sclk_prev = '0' then
                    shift_reg_rx <= shift_reg_rx(DATA_WIDTH-2 downto 0) & mosi;
                    bit_counter <= bit_counter + 1;
                end if;

                -- Falling edge of SCLK - shift out MISO
                if sclk = '0' and sclk_prev = '1' then
                    shift_reg_tx <= shift_reg_tx(DATA_WIDTH-2 downto 0) & '0';
                end if;

                miso <= shift_reg_tx(DATA_WIDTH-1);
            else
                bit_counter <= 0;
                -- CS deasserted - byte complete
                if cs_n_prev = '0' then
                    if bit_counter = DATA_WIDTH then
                        rx_data <= shift_reg_rx;
                        rx_valid <= '1';
                    end if;
                end if;
            end if;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity SPIMaster_TB is
end SPIMaster_TB;

architecture Test of SPIMaster_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_start : bit;
    signal tb_cpol, tb_cpha : bit;
    signal tb_data_in, tb_data_out : bit_vector(7 downto 0);
    signal tb_busy, tb_done : bit;
    signal tb_sclk, tb_mosi, tb_miso, tb_cs_n : bit;
begin
    UUT: entity work.SPIMaster
        generic map (CLK_DIV => 4, DATA_WIDTH => 8)
        port map (
            clk => tb_clk, reset => tb_reset,
            start => tb_start,
            cpol => tb_cpol, cpha => tb_cpha,
            data_in => tb_data_in, data_out => tb_data_out,
            busy => tb_busy, done => tb_done,
            sclk => tb_sclk, mosi => tb_mosi,
            miso => tb_miso, cs_n => tb_cs_n
        );

    -- Loopback
    tb_miso <= tb_mosi;

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_start <= '0';
        tb_cpol <= '0';
        tb_cpha <= '0';
        tb_data_in <= (others => '0');
        wait for 50 ns;

        tb_reset <= '0';
        wait for 20 ns;

        -- Send byte in Mode 0 (CPOL=0, CPHA=0)
        tb_data_in <= "10101010";
        tb_start <= '1';
        wait for 10 ns;
        tb_start <= '0';

        wait until tb_done = '1';
        wait for 100 ns;

        -- Send byte in Mode 3 (CPOL=1, CPHA=1)
        tb_cpol <= '1';
        tb_cpha <= '1';
        tb_data_in <= "11001100";
        tb_start <= '1';
        wait for 10 ns;
        tb_start <= '0';

        wait until tb_done = '1';
        wait for 100 ns;

        wait;
    end process;
end Test;
