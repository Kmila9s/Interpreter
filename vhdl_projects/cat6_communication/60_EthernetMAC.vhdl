-- ============================================================================
-- VHDL Project: EthernetMAC
-- Category: Communication Protocols
-- Complexity: Very High
-- Description: Basic Ethernet MAC layer with MII interface
--              Frame handling, CRC, preamble generation
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity EthernetMAC is
    port (
        clk_25mhz   : in  bit;  -- 25 MHz for 100 Mbps
        reset       : in  bit;
        -- TX interface
        tx_data     : in  bit_vector(7 downto 0);
        tx_valid    : in  bit;
        tx_last     : in  bit;
        tx_ready    : out bit;
        -- RX interface
        rx_data     : out bit_vector(7 downto 0);
        rx_valid    : out bit;
        rx_last     : out bit;
        rx_error    : out bit;
        -- MII interface
        mii_tx_clk  : in  bit;
        mii_tx_en   : out bit;
        mii_txd     : out bit_vector(3 downto 0);
        mii_rx_clk  : in  bit;
        mii_rx_dv   : in  bit;
        mii_rxd     : in  bit_vector(3 downto 0);
        mii_rx_er   : in  bit
    );
end EthernetMAC;

architecture Behavioral of EthernetMAC is
    -- CRC-32 polynomial
    constant CRC_POLY : bit_vector(31 downto 0) := "00000100110000010001110110110111";

    type tx_state_type is (IDLE, PREAMBLE, SFD, DATA_LOW, DATA_HIGH, CRC);
    type rx_state_type is (IDLE, PREAMBLE, DATA_LOW, DATA_HIGH, CHECK_CRC);

    signal tx_state : tx_state_type;
    signal rx_state : rx_state_type;

    signal tx_preamble_cnt : integer range 0 to 15;
    signal tx_byte_reg : bit_vector(7 downto 0);
    signal tx_crc : bit_vector(31 downto 0);
    signal tx_crc_cnt : integer range 0 to 8;

    signal rx_byte_reg : bit_vector(7 downto 0);
    signal rx_crc : bit_vector(31 downto 0);
    signal rx_nibble_cnt : integer range 0 to 1;

    function crc32_update(crc : bit_vector; data : bit_vector) return bit_vector is
        variable result : bit_vector(31 downto 0);
        variable d : bit;
    begin
        result := crc;
        for i in 0 to data'length-1 loop
            d := data(i) xor result(31);
            result := result(30 downto 0) & '0';
            if d = '1' then
                result := result xor CRC_POLY;
            end if;
        end loop;
        return result;
    end function;

begin
    -- TX state machine
    tx_proc: process(mii_tx_clk, reset)
    begin
        if reset = '1' then
            tx_state <= IDLE;
            mii_tx_en <= '0';
            mii_txd <= "0000";
            tx_ready <= '1';
            tx_crc <= (others => '1');
        elsif mii_tx_clk'event and mii_tx_clk = '1' then
            case tx_state is
                when IDLE =>
                    mii_tx_en <= '0';
                    tx_ready <= '1';
                    tx_crc <= (others => '1');
                    if tx_valid = '1' then
                        tx_byte_reg <= tx_data;
                        tx_ready <= '0';
                        tx_preamble_cnt <= 0;
                        tx_state <= PREAMBLE;
                    end if;

                when PREAMBLE =>
                    mii_tx_en <= '1';
                    mii_txd <= "0101";  -- Preamble pattern
                    tx_preamble_cnt <= tx_preamble_cnt + 1;
                    if tx_preamble_cnt = 14 then
                        tx_state <= SFD;
                    end if;

                when SFD =>
                    mii_txd <= "1101";  -- SFD
                    tx_state <= DATA_LOW;

                when DATA_LOW =>
                    mii_txd <= tx_byte_reg(3 downto 0);
                    tx_crc <= crc32_update(tx_crc, tx_byte_reg(3 downto 0));
                    tx_state <= DATA_HIGH;

                when DATA_HIGH =>
                    mii_txd <= tx_byte_reg(7 downto 4);
                    tx_crc <= crc32_update(tx_crc, tx_byte_reg(7 downto 4));
                    if tx_last = '1' then
                        tx_crc_cnt <= 0;
                        tx_state <= CRC;
                    else
                        tx_ready <= '1';
                        if tx_valid = '1' then
                            tx_byte_reg <= tx_data;
                            tx_ready <= '0';
                            tx_state <= DATA_LOW;
                        else
                            tx_state <= IDLE;  -- Underrun
                        end if;
                    end if;

                when CRC =>
                    case tx_crc_cnt is
                        when 0 => mii_txd <= not tx_crc(3 downto 0);
                        when 1 => mii_txd <= not tx_crc(7 downto 4);
                        when 2 => mii_txd <= not tx_crc(11 downto 8);
                        when 3 => mii_txd <= not tx_crc(15 downto 12);
                        when 4 => mii_txd <= not tx_crc(19 downto 16);
                        when 5 => mii_txd <= not tx_crc(23 downto 20);
                        when 6 => mii_txd <= not tx_crc(27 downto 24);
                        when 7 => mii_txd <= not tx_crc(31 downto 28);
                        when others => null;
                    end case;
                    tx_crc_cnt <= tx_crc_cnt + 1;
                    if tx_crc_cnt = 7 then
                        tx_state <= IDLE;
                    end if;
            end case;
        end if;
    end process;

    -- RX state machine
    rx_proc: process(mii_rx_clk, reset)
    begin
        if reset = '1' then
            rx_state <= IDLE;
            rx_valid <= '0';
            rx_error <= '0';
            rx_crc <= (others => '1');
        elsif mii_rx_clk'event and mii_rx_clk = '1' then
            rx_valid <= '0';
            rx_last <= '0';
            rx_error <= '0';

            case rx_state is
                when IDLE =>
                    rx_crc <= (others => '1');
                    if mii_rx_dv = '1' then
                        if mii_rxd = "1101" then  -- SFD detected
                            rx_state <= DATA_LOW;
                        elsif mii_rxd = "0101" then  -- Still preamble
                            rx_state <= PREAMBLE;
                        end if;
                    end if;

                when PREAMBLE =>
                    if mii_rx_dv = '1' then
                        if mii_rxd = "1101" then
                            rx_state <= DATA_LOW;
                        end if;
                    else
                        rx_state <= IDLE;
                    end if;

                when DATA_LOW =>
                    if mii_rx_dv = '1' then
                        rx_byte_reg(3 downto 0) <= mii_rxd;
                        rx_crc <= crc32_update(rx_crc, mii_rxd);
                        rx_state <= DATA_HIGH;
                    else
                        rx_state <= CHECK_CRC;
                    end if;

                when DATA_HIGH =>
                    if mii_rx_dv = '1' then
                        rx_byte_reg(7 downto 4) <= mii_rxd;
                        rx_crc <= crc32_update(rx_crc, mii_rxd);
                        rx_data <= rx_byte_reg(3 downto 0) & mii_rxd;
                        rx_valid <= '1';
                        rx_state <= DATA_LOW;
                    else
                        rx_state <= CHECK_CRC;
                    end if;

                    if mii_rx_er = '1' then
                        rx_error <= '1';
                    end if;

                when CHECK_CRC =>
                    rx_last <= '1';
                    -- CRC check: should equal magic value
                    if rx_crc /= "11000111000001001101110101111011" then
                        rx_error <= '1';
                    end if;
                    rx_state <= IDLE;
            end case;
        end if;
    end process;
end Behavioral;
