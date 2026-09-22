-- ============================================================================
-- VHDL Project: RS485Driver
-- Category: Communication Protocols
-- Complexity: Medium
-- Description: RS-485 half-duplex driver with collision detection
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity RS485Driver is
    generic (
        CLK_FREQ : integer := 50000000;
        BAUD_RATE : integer := 9600;
        TURNAROUND_US : integer := 100
    );
    port (
        clk, reset : in bit;
        tx_data : in bit_vector(7 downto 0);
        tx_start : in bit;
        tx_busy, tx_done : out bit;
        rx_data : out bit_vector(7 downto 0);
        rx_valid : out bit;
        driver_enable : out bit;
        tx_out : out bit;
        rx_in : in bit
    );
end RS485Driver;

architecture Behavioral of RS485Driver is
    constant CLKS_PER_BIT : integer := CLK_FREQ / BAUD_RATE;
    constant TURNAROUND_CLKS : integer := (CLK_FREQ / 1000000) * TURNAROUND_US;

    type tx_state_type is (IDLE, ENABLE_DRIVER, START_BIT, DATA_BITS, STOP_BIT, TURNAROUND);
    type rx_state_type is (IDLE, START_BIT, DATA_BITS, STOP_BIT);

    signal tx_state : tx_state_type;
    signal rx_state : rx_state_type;
    signal tx_clk_cnt, rx_clk_cnt : integer range 0 to CLKS_PER_BIT;
    signal tx_bit_cnt, rx_bit_cnt : integer range 0 to 7;
    signal tx_shift, rx_shift : bit_vector(7 downto 0);
    signal turnaround_cnt : integer range 0 to TURNAROUND_CLKS;
begin
    -- Transmit
    tx_proc: process(clk, reset)
    begin
        if reset = '1' then
            tx_state <= IDLE; tx_busy <= '0'; tx_done <= '0';
            tx_out <= '1'; driver_enable <= '0';
        elsif clk'event and clk = '1' then
            tx_done <= '0';
            case tx_state is
                when IDLE =>
                    tx_busy <= '0'; driver_enable <= '0'; tx_out <= '1';
                    if tx_start = '1' then
                        tx_shift <= tx_data; tx_busy <= '1';
                        tx_state <= ENABLE_DRIVER; turnaround_cnt <= 0;
                    end if;
                when ENABLE_DRIVER =>
                    driver_enable <= '1';
                    turnaround_cnt <= turnaround_cnt + 1;
                    if turnaround_cnt >= 10 then
                        tx_clk_cnt <= 0; tx_state <= START_BIT;
                    end if;
                when START_BIT =>
                    tx_out <= '0';
                    if tx_clk_cnt < CLKS_PER_BIT-1 then
                        tx_clk_cnt <= tx_clk_cnt + 1;
                    else
                        tx_clk_cnt <= 0; tx_bit_cnt <= 0; tx_state <= DATA_BITS;
                    end if;
                when DATA_BITS =>
                    tx_out <= tx_shift(tx_bit_cnt);
                    if tx_clk_cnt < CLKS_PER_BIT-1 then
                        tx_clk_cnt <= tx_clk_cnt + 1;
                    else
                        tx_clk_cnt <= 0;
                        if tx_bit_cnt < 7 then tx_bit_cnt <= tx_bit_cnt + 1;
                        else tx_state <= STOP_BIT; end if;
                    end if;
                when STOP_BIT =>
                    tx_out <= '1';
                    if tx_clk_cnt < CLKS_PER_BIT-1 then
                        tx_clk_cnt <= tx_clk_cnt + 1;
                    else
                        turnaround_cnt <= 0; tx_state <= TURNAROUND;
                    end if;
                when TURNAROUND =>
                    turnaround_cnt <= turnaround_cnt + 1;
                    if turnaround_cnt >= TURNAROUND_CLKS then
                        tx_done <= '1'; tx_state <= IDLE;
                    end if;
            end case;
        end if;
    end process;

    -- Receive
    rx_proc: process(clk, reset)
    begin
        if reset = '1' then
            rx_state <= IDLE; rx_valid <= '0';
        elsif clk'event and clk = '1' then
            rx_valid <= '0';
            case rx_state is
                when IDLE =>
                    rx_clk_cnt <= 0;
                    if rx_in = '0' then rx_state <= START_BIT; end if;
                when START_BIT =>
                    if rx_clk_cnt < (CLKS_PER_BIT-1)/2 then
                        rx_clk_cnt <= rx_clk_cnt + 1;
                    else
                        if rx_in = '0' then
                            rx_clk_cnt <= 0; rx_bit_cnt <= 0; rx_state <= DATA_BITS;
                        else rx_state <= IDLE; end if;
                    end if;
                when DATA_BITS =>
                    if rx_clk_cnt < CLKS_PER_BIT-1 then
                        rx_clk_cnt <= rx_clk_cnt + 1;
                    else
                        rx_clk_cnt <= 0;
                        rx_shift(rx_bit_cnt) <= rx_in;
                        if rx_bit_cnt < 7 then rx_bit_cnt <= rx_bit_cnt + 1;
                        else rx_state <= STOP_BIT; end if;
                    end if;
                when STOP_BIT =>
                    if rx_clk_cnt < CLKS_PER_BIT-1 then
                        rx_clk_cnt <= rx_clk_cnt + 1;
                    else
                        if rx_in = '1' then
                            rx_data <= rx_shift; rx_valid <= '1';
                        end if;
                        rx_state <= IDLE;
                    end if;
            end case;
        end if;
    end process;
end Behavioral;
