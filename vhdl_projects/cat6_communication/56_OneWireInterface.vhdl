-- ============================================================================
-- VHDL Project: OneWireInterface
-- Category: Communication Protocols
-- Complexity: Medium-High
-- Description: Dallas 1-Wire protocol master (DS18B20, etc.)
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity OneWireInterface is
    generic (CLK_FREQ : integer := 50000000);
    port (
        clk, reset : in bit;
        cmd_reset, cmd_write, cmd_read : in bit;
        write_data : in bit_vector(7 downto 0);
        read_data : out bit_vector(7 downto 0);
        busy, presence, done : out bit;
        ow_out, ow_oe : out bit;
        ow_in : in bit
    );
end OneWireInterface;

architecture Behavioral of OneWireInterface is
    constant US_CNT : integer := CLK_FREQ / 1000000;
    type state_type is (IDLE, RESET_PULSE, RESET_WAIT, PRESENCE_SAMPLE,
                        WRITE_BIT, WRITE_LOW, WRITE_RELEASE, READ_BIT,
                        READ_LOW, READ_SAMPLE, READ_RELEASE);
    signal state : state_type;
    signal timer : integer range 0 to 65535;
    signal bit_cnt : integer range 0 to 7;
    signal data_reg : bit_vector(7 downto 0);
begin
    process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE; busy <= '0'; done <= '0';
            ow_out <= '1'; ow_oe <= '0'; presence <= '0';
        elsif clk'event and clk = '1' then
            done <= '0';
            case state is
                when IDLE =>
                    busy <= '0'; ow_oe <= '0';
                    if cmd_reset = '1' then
                        state <= RESET_PULSE; timer <= 0; busy <= '1';
                    elsif cmd_write = '1' then
                        data_reg <= write_data; bit_cnt <= 0;
                        state <= WRITE_BIT; busy <= '1';
                    elsif cmd_read = '1' then
                        bit_cnt <= 0; state <= READ_BIT; busy <= '1';
                    end if;

                when RESET_PULSE =>
                    ow_oe <= '1'; ow_out <= '0';
                    timer <= timer + 1;
                    if timer >= 480 * US_CNT then
                        timer <= 0; state <= RESET_WAIT;
                    end if;

                when RESET_WAIT =>
                    ow_oe <= '0'; timer <= timer + 1;
                    if timer >= 70 * US_CNT then
                        timer <= 0; state <= PRESENCE_SAMPLE;
                    end if;

                when PRESENCE_SAMPLE =>
                    presence <= not ow_in; timer <= timer + 1;
                    if timer >= 410 * US_CNT then
                        done <= '1'; state <= IDLE;
                    end if;

                when WRITE_BIT =>
                    timer <= 0; state <= WRITE_LOW;

                when WRITE_LOW =>
                    ow_oe <= '1'; ow_out <= '0'; timer <= timer + 1;
                    if data_reg(bit_cnt) = '1' then
                        if timer >= 6 * US_CNT then
                            state <= WRITE_RELEASE; timer <= 0;
                        end if;
                    else
                        if timer >= 60 * US_CNT then
                            state <= WRITE_RELEASE; timer <= 0;
                        end if;
                    end if;

                when WRITE_RELEASE =>
                    ow_oe <= '0'; timer <= timer + 1;
                    if timer >= 10 * US_CNT then
                        if bit_cnt = 7 then done <= '1'; state <= IDLE;
                        else bit_cnt <= bit_cnt + 1; state <= WRITE_BIT; end if;
                    end if;

                when READ_BIT =>
                    timer <= 0; state <= READ_LOW;

                when READ_LOW =>
                    ow_oe <= '1'; ow_out <= '0'; timer <= timer + 1;
                    if timer >= 6 * US_CNT then
                        ow_oe <= '0'; timer <= 0; state <= READ_SAMPLE;
                    end if;

                when READ_SAMPLE =>
                    timer <= timer + 1;
                    if timer >= 9 * US_CNT then
                        data_reg(bit_cnt) <= ow_in; timer <= 0; state <= READ_RELEASE;
                    end if;

                when READ_RELEASE =>
                    timer <= timer + 1;
                    if timer >= 55 * US_CNT then
                        if bit_cnt = 7 then
                            read_data <= data_reg; done <= '1'; state <= IDLE;
                        else bit_cnt <= bit_cnt + 1; state <= READ_BIT; end if;
                    end if;
            end case;
        end if;
    end process;
end Behavioral;
