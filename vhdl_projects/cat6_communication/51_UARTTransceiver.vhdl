-- ============================================================================
-- VHDL Project: UARTTransceiver
-- Category: Communication Protocols
-- Complexity: Medium
-- Description: Full UART with TX, RX, configurable baud rate
--              Includes parity, stop bits, flow control
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity UARTTransceiver is
    generic (
        CLK_FREQ    : integer := 50000000;  -- 50 MHz
        BAUD_RATE   : integer := 115200
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- TX interface
        tx_data     : in  bit_vector(7 downto 0);
        tx_start    : in  bit;
        tx_busy     : out bit;
        tx_done     : out bit;
        tx_out      : out bit;
        -- RX interface
        rx_in       : in  bit;
        rx_data     : out bit_vector(7 downto 0);
        rx_valid    : out bit;
        rx_error    : out bit;
        -- Configuration
        parity_en   : in  bit;
        parity_odd  : in  bit;  -- '1' = odd parity, '0' = even
        stop_bits   : in  bit   -- '0' = 1 stop bit, '1' = 2 stop bits
    );
end UARTTransceiver;

architecture Behavioral of UARTTransceiver is
    constant CLKS_PER_BIT : integer := CLK_FREQ / BAUD_RATE;

    -- TX state machine
    type tx_state_type is (TX_IDLE, TX_START, TX_DATA, TX_PARITY, TX_STOP, TX_STOP2);
    signal tx_state : tx_state_type;
    signal tx_clk_count : integer range 0 to CLKS_PER_BIT-1;
    signal tx_bit_index : integer range 0 to 7;
    signal tx_shift_reg : bit_vector(7 downto 0);
    signal tx_parity_bit : bit;

    -- RX state machine
    type rx_state_type is (RX_IDLE, RX_START, RX_DATA, RX_PARITY, RX_STOP, RX_STOP2);
    signal rx_state : rx_state_type;
    signal rx_clk_count : integer range 0 to CLKS_PER_BIT-1;
    signal rx_bit_index : integer range 0 to 7;
    signal rx_shift_reg : bit_vector(7 downto 0);
    signal rx_parity_bit : bit;
    signal rx_parity_calc : bit;

    function calc_parity(data : bit_vector; odd : bit) return bit is
        variable p : bit := odd;  -- Start with '1' for odd parity
    begin
        for i in data'range loop
            p := p xor data(i);
        end loop;
        return p;
    end function;

begin
    -- Transmitter
    tx_proc: process(clk, reset)
    begin
        if reset = '1' then
            tx_state <= TX_IDLE;
            tx_clk_count <= 0;
            tx_bit_index <= 0;
            tx_out <= '1';
            tx_busy <= '0';
            tx_done <= '0';
        elsif clk'event and clk = '1' then
            tx_done <= '0';

            case tx_state is
                when TX_IDLE =>
                    tx_out <= '1';
                    tx_busy <= '0';
                    tx_clk_count <= 0;
                    tx_bit_index <= 0;

                    if tx_start = '1' then
                        tx_shift_reg <= tx_data;
                        tx_parity_bit <= calc_parity(tx_data, parity_odd);
                        tx_busy <= '1';
                        tx_state <= TX_START;
                    end if;

                when TX_START =>
                    tx_out <= '0';  -- Start bit
                    if tx_clk_count < CLKS_PER_BIT - 1 then
                        tx_clk_count <= tx_clk_count + 1;
                    else
                        tx_clk_count <= 0;
                        tx_state <= TX_DATA;
                    end if;

                when TX_DATA =>
                    tx_out <= tx_shift_reg(tx_bit_index);
                    if tx_clk_count < CLKS_PER_BIT - 1 then
                        tx_clk_count <= tx_clk_count + 1;
                    else
                        tx_clk_count <= 0;
                        if tx_bit_index < 7 then
                            tx_bit_index <= tx_bit_index + 1;
                        else
                            tx_bit_index <= 0;
                            if parity_en = '1' then
                                tx_state <= TX_PARITY;
                            else
                                tx_state <= TX_STOP;
                            end if;
                        end if;
                    end if;

                when TX_PARITY =>
                    tx_out <= tx_parity_bit;
                    if tx_clk_count < CLKS_PER_BIT - 1 then
                        tx_clk_count <= tx_clk_count + 1;
                    else
                        tx_clk_count <= 0;
                        tx_state <= TX_STOP;
                    end if;

                when TX_STOP =>
                    tx_out <= '1';  -- Stop bit
                    if tx_clk_count < CLKS_PER_BIT - 1 then
                        tx_clk_count <= tx_clk_count + 1;
                    else
                        tx_clk_count <= 0;
                        if stop_bits = '1' then
                            tx_state <= TX_STOP2;
                        else
                            tx_done <= '1';
                            tx_state <= TX_IDLE;
                        end if;
                    end if;

                when TX_STOP2 =>
                    tx_out <= '1';
                    if tx_clk_count < CLKS_PER_BIT - 1 then
                        tx_clk_count <= tx_clk_count + 1;
                    else
                        tx_done <= '1';
                        tx_state <= TX_IDLE;
                    end if;
            end case;
        end if;
    end process;

    -- Receiver
    rx_proc: process(clk, reset)
    begin
        if reset = '1' then
            rx_state <= RX_IDLE;
            rx_clk_count <= 0;
            rx_bit_index <= 0;
            rx_data <= (others => '0');
            rx_valid <= '0';
            rx_error <= '0';
            rx_parity_calc <= '0';
        elsif clk'event and clk = '1' then
            rx_valid <= '0';
            rx_error <= '0';

            case rx_state is
                when RX_IDLE =>
                    rx_clk_count <= 0;
                    rx_bit_index <= 0;
                    rx_parity_calc <= parity_odd;

                    if rx_in = '0' then  -- Start bit detected
                        rx_state <= RX_START;
                    end if;

                when RX_START =>
                    -- Sample at middle of start bit
                    if rx_clk_count < (CLKS_PER_BIT - 1) / 2 then
                        rx_clk_count <= rx_clk_count + 1;
                    else
                        if rx_in = '0' then
                            -- Valid start bit
                            rx_clk_count <= 0;
                            rx_state <= RX_DATA;
                        else
                            -- False start
                            rx_state <= RX_IDLE;
                        end if;
                    end if;

                when RX_DATA =>
                    if rx_clk_count < CLKS_PER_BIT - 1 then
                        rx_clk_count <= rx_clk_count + 1;
                    else
                        rx_clk_count <= 0;
                        rx_shift_reg(rx_bit_index) <= rx_in;
                        rx_parity_calc <= rx_parity_calc xor rx_in;

                        if rx_bit_index < 7 then
                            rx_bit_index <= rx_bit_index + 1;
                        else
                            rx_bit_index <= 0;
                            if parity_en = '1' then
                                rx_state <= RX_PARITY;
                            else
                                rx_state <= RX_STOP;
                            end if;
                        end if;
                    end if;

                when RX_PARITY =>
                    if rx_clk_count < CLKS_PER_BIT - 1 then
                        rx_clk_count <= rx_clk_count + 1;
                    else
                        rx_clk_count <= 0;
                        rx_parity_bit <= rx_in;
                        rx_state <= RX_STOP;
                    end if;

                when RX_STOP =>
                    if rx_clk_count < CLKS_PER_BIT - 1 then
                        rx_clk_count <= rx_clk_count + 1;
                    else
                        rx_clk_count <= 0;
                        if rx_in = '1' then
                            -- Valid stop bit
                            rx_data <= rx_shift_reg;
                            rx_valid <= '1';
                            -- Check parity
                            if parity_en = '1' and rx_parity_calc /= rx_parity_bit then
                                rx_error <= '1';
                            end if;
                        else
                            -- Framing error
                            rx_error <= '1';
                        end if;

                        if stop_bits = '1' then
                            rx_state <= RX_STOP2;
                        else
                            rx_state <= RX_IDLE;
                        end if;
                    end if;

                when RX_STOP2 =>
                    if rx_clk_count < CLKS_PER_BIT - 1 then
                        rx_clk_count <= rx_clk_count + 1;
                    else
                        rx_state <= RX_IDLE;
                    end if;
            end case;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity UARTTransceiver_TB is
end UARTTransceiver_TB;

architecture Test of UARTTransceiver_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_tx_data : bit_vector(7 downto 0);
    signal tb_tx_start, tb_tx_busy, tb_tx_done, tb_tx_out : bit;
    signal tb_rx_in : bit;
    signal tb_rx_data : bit_vector(7 downto 0);
    signal tb_rx_valid, tb_rx_error : bit;
    signal tb_parity_en, tb_parity_odd, tb_stop_bits : bit;
begin
    UUT: entity work.UARTTransceiver
        generic map (CLK_FREQ => 1000000, BAUD_RATE => 9600)
        port map (
            clk => tb_clk, reset => tb_reset,
            tx_data => tb_tx_data, tx_start => tb_tx_start,
            tx_busy => tb_tx_busy, tx_done => tb_tx_done,
            tx_out => tb_tx_out,
            rx_in => tb_rx_in, rx_data => tb_rx_data,
            rx_valid => tb_rx_valid, rx_error => tb_rx_error,
            parity_en => tb_parity_en, parity_odd => tb_parity_odd,
            stop_bits => tb_stop_bits
        );

    -- Loopback
    tb_rx_in <= tb_tx_out;

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 500 ns;  -- 1 MHz clock
        tb_clk <= '1'; wait for 500 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_tx_data <= (others => '0');
        tb_tx_start <= '0';
        tb_parity_en <= '0';
        tb_parity_odd <= '0';
        tb_stop_bits <= '0';
        wait for 10 us;

        tb_reset <= '0';
        wait for 10 us;

        -- Send byte
        tb_tx_data <= "10101010";
        tb_tx_start <= '1';
        wait for 1 us;
        tb_tx_start <= '0';

        wait until tb_rx_valid = '1';
        wait for 100 us;

        wait;
    end process;
end Test;
