-- ============================================================================
-- VHDL Project: PS2Controller
-- Category: Communication Protocols
-- Complexity: Medium
-- Description: PS/2 keyboard/mouse interface controller
--              Bidirectional communication, scan code handling
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity PS2Controller is
    generic (
        CLK_FREQ : integer := 50000000
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- PS/2 interface
        ps2_clk_in  : in  bit;
        ps2_data_in : in  bit;
        ps2_clk_out : out bit;
        ps2_data_out: out bit;
        ps2_clk_oe  : out bit;
        ps2_data_oe : out bit;
        -- Receive
        rx_data     : out bit_vector(7 downto 0);
        rx_valid    : out bit;
        rx_error    : out bit;
        -- Transmit
        tx_data     : in  bit_vector(7 downto 0);
        tx_start    : in  bit;
        tx_busy     : out bit;
        tx_done     : out bit
    );
end PS2Controller;

architecture Behavioral of PS2Controller is
    type rx_state_type is (RX_IDLE, RX_DATA, RX_PARITY, RX_STOP);
    type tx_state_type is (TX_IDLE, TX_REQUEST, TX_START, TX_DATA, TX_PARITY, TX_STOP, TX_ACK);

    signal rx_state : rx_state_type;
    signal tx_state : tx_state_type;

    signal ps2_clk_sync : bit_vector(2 downto 0);
    signal ps2_data_sync : bit_vector(2 downto 0);
    signal ps2_clk_fall : bit;

    signal rx_shift : bit_vector(7 downto 0);
    signal rx_bit_cnt : integer range 0 to 7;
    signal rx_parity : bit;

    signal tx_shift : bit_vector(7 downto 0);
    signal tx_bit_cnt : integer range 0 to 7;
    signal tx_parity : bit;
    signal tx_timer : integer range 0 to 65535;

    function calc_parity(d : bit_vector) return bit is
        variable p : bit := '1';
    begin
        for i in d'range loop
            p := p xor d(i);
        end loop;
        return p;
    end function;

begin
    -- Synchronize and edge detect
    sync_proc: process(clk, reset)
    begin
        if reset = '1' then
            ps2_clk_sync <= "111";
            ps2_data_sync <= "111";
        elsif clk'event and clk = '1' then
            ps2_clk_sync <= ps2_clk_sync(1 downto 0) & ps2_clk_in;
            ps2_data_sync <= ps2_data_sync(1 downto 0) & ps2_data_in;
        end if;
    end process;

    ps2_clk_fall <= '1' when ps2_clk_sync(2) = '1' and ps2_clk_sync(1) = '0' else '0';

    -- Receive FSM
    rx_proc: process(clk, reset)
    begin
        if reset = '1' then
            rx_state <= RX_IDLE;
            rx_valid <= '0';
            rx_error <= '0';
            rx_data <= (others => '0');
        elsif clk'event and clk = '1' then
            rx_valid <= '0';
            rx_error <= '0';

            case rx_state is
                when RX_IDLE =>
                    if ps2_clk_fall = '1' and ps2_data_sync(2) = '0' then
                        rx_state <= RX_DATA;
                        rx_bit_cnt <= 0;
                        rx_parity <= '0';
                    end if;

                when RX_DATA =>
                    if ps2_clk_fall = '1' then
                        rx_shift(rx_bit_cnt) <= ps2_data_sync(2);
                        rx_parity <= rx_parity xor ps2_data_sync(2);
                        if rx_bit_cnt = 7 then
                            rx_state <= RX_PARITY;
                        else
                            rx_bit_cnt <= rx_bit_cnt + 1;
                        end if;
                    end if;

                when RX_PARITY =>
                    if ps2_clk_fall = '1' then
                        if (rx_parity xor ps2_data_sync(2)) = '1' then
                            rx_state <= RX_STOP;
                        else
                            rx_error <= '1';
                            rx_state <= RX_IDLE;
                        end if;
                    end if;

                when RX_STOP =>
                    if ps2_clk_fall = '1' then
                        if ps2_data_sync(2) = '1' then
                            rx_data <= rx_shift;
                            rx_valid <= '1';
                        else
                            rx_error <= '1';
                        end if;
                        rx_state <= RX_IDLE;
                    end if;
            end case;
        end if;
    end process;

    -- Transmit FSM
    tx_proc: process(clk, reset)
    begin
        if reset = '1' then
            tx_state <= TX_IDLE;
            tx_busy <= '0';
            tx_done <= '0';
            ps2_clk_oe <= '0';
            ps2_data_oe <= '0';
            ps2_clk_out <= '1';
            ps2_data_out <= '1';
        elsif clk'event and clk = '1' then
            tx_done <= '0';

            case tx_state is
                when TX_IDLE =>
                    tx_busy <= '0';
                    ps2_clk_oe <= '0';
                    ps2_data_oe <= '0';
                    if tx_start = '1' then
                        tx_shift <= tx_data;
                        tx_parity <= calc_parity(tx_data);
                        tx_busy <= '1';
                        tx_state <= TX_REQUEST;
                        tx_timer <= 0;
                    end if;

                when TX_REQUEST =>
                    ps2_clk_oe <= '1';
                    ps2_clk_out <= '0';
                    tx_timer <= tx_timer + 1;
                    if tx_timer >= 5000 then
                        tx_state <= TX_START;
                    end if;

                when TX_START =>
                    ps2_data_oe <= '1';
                    ps2_data_out <= '0';
                    ps2_clk_oe <= '0';
                    tx_bit_cnt <= 0;
                    tx_state <= TX_DATA;

                when TX_DATA =>
                    if ps2_clk_fall = '1' then
                        ps2_data_out <= tx_shift(tx_bit_cnt);
                        if tx_bit_cnt = 7 then
                            tx_state <= TX_PARITY;
                        else
                            tx_bit_cnt <= tx_bit_cnt + 1;
                        end if;
                    end if;

                when TX_PARITY =>
                    if ps2_clk_fall = '1' then
                        ps2_data_out <= tx_parity;
                        tx_state <= TX_STOP;
                    end if;

                when TX_STOP =>
                    if ps2_clk_fall = '1' then
                        ps2_data_oe <= '0';
                        tx_state <= TX_ACK;
                    end if;

                when TX_ACK =>
                    if ps2_clk_fall = '1' then
                        tx_done <= '1';
                        tx_state <= TX_IDLE;
                    end if;
            end case;
        end if;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity PS2Controller_TB is
end PS2Controller_TB;

architecture Test of PS2Controller_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_ps2_clk_in, tb_ps2_data_in : bit;
    signal tb_rx_data : bit_vector(7 downto 0);
    signal tb_rx_valid, tb_rx_error : bit;
begin
    UUT: entity work.PS2Controller
        port map (
            clk => tb_clk, reset => tb_reset,
            ps2_clk_in => tb_ps2_clk_in, ps2_data_in => tb_ps2_data_in,
            rx_data => tb_rx_data, rx_valid => tb_rx_valid, rx_error => tb_rx_error
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 10 ns;
        tb_clk <= '1'; wait for 10 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_ps2_clk_in <= '1';
        tb_ps2_data_in <= '1';
        wait for 100 ns;
        tb_reset <= '0';
        wait for 100 ns;
        wait;
    end process;
end Test;
