-- ============================================================================
-- VHDL Project: ParallelInterface
-- Category: Communication Protocols
-- Complexity: Medium
-- Description: 8-bit parallel bus interface with handshaking
--              Supports bidirectional data transfer
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ParallelInterface is
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Host interface
        host_data_in  : in  bit_vector(7 downto 0);
        host_data_out : out bit_vector(7 downto 0);
        host_write    : in  bit;
        host_read     : in  bit;
        host_ready    : out bit;
        -- External bus
        bus_data_out  : out bit_vector(7 downto 0);
        bus_data_in   : in  bit_vector(7 downto 0);
        bus_oe        : out bit;
        -- Handshake signals
        strobe_out    : out bit;
        ack_in        : in  bit;
        strobe_in     : in  bit;
        ack_out       : out bit
    );
end ParallelInterface;

architecture Behavioral of ParallelInterface is
    type tx_state_type is (TX_IDLE, TX_STROBE, TX_WAIT_ACK);
    type rx_state_type is (RX_IDLE, RX_LATCH, RX_ACK);
    signal tx_state : tx_state_type;
    signal rx_state : rx_state_type;
    signal rx_data_reg : bit_vector(7 downto 0);
    signal tx_data_reg : bit_vector(7 downto 0);
    signal strobe_sync : bit_vector(1 downto 0);
    signal ack_sync : bit_vector(1 downto 0);
begin
    sync_proc: process(clk, reset)
    begin
        if reset = '1' then
            strobe_sync <= "00";
            ack_sync <= "00";
        elsif clk'event and clk = '1' then
            strobe_sync <= strobe_sync(0) & strobe_in;
            ack_sync <= ack_sync(0) & ack_in;
        end if;
    end process;

    -- Transmit FSM
    tx_proc: process(clk, reset)
    begin
        if reset = '1' then
            tx_state <= TX_IDLE;
            strobe_out <= '0';
            bus_oe <= '0';
        elsif clk'event and clk = '1' then
            case tx_state is
                when TX_IDLE =>
                    strobe_out <= '0';
                    bus_oe <= '0';
                    if host_write = '1' then
                        tx_data_reg <= host_data_in;
                        tx_state <= TX_STROBE;
                    end if;
                when TX_STROBE =>
                    bus_oe <= '1';
                    bus_data_out <= tx_data_reg;
                    strobe_out <= '1';
                    tx_state <= TX_WAIT_ACK;
                when TX_WAIT_ACK =>
                    if ack_sync(1) = '1' then
                        strobe_out <= '0';
                        tx_state <= TX_IDLE;
                    end if;
            end case;
        end if;
    end process;

    -- Receive FSM
    rx_proc: process(clk, reset)
    begin
        if reset = '1' then
            rx_state <= RX_IDLE;
            ack_out <= '0';
            rx_data_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            case rx_state is
                when RX_IDLE =>
                    ack_out <= '0';
                    if strobe_sync(1) = '1' then
                        rx_state <= RX_LATCH;
                    end if;
                when RX_LATCH =>
                    rx_data_reg <= bus_data_in;
                    rx_state <= RX_ACK;
                when RX_ACK =>
                    ack_out <= '1';
                    if strobe_sync(1) = '0' then
                        ack_out <= '0';
                        rx_state <= RX_IDLE;
                    end if;
            end case;
        end if;
    end process;

    host_data_out <= rx_data_reg;
    host_ready <= '1' when tx_state = TX_IDLE else '0';
end Behavioral;
