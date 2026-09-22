-- ============================================================================
-- VHDL Project: I2CMaster
-- Category: Communication Protocols
-- Complexity: High
-- Description: I2C Master controller with multi-byte transfer
--              Start, stop, repeated start, ACK/NACK handling
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity I2CMaster is
    generic (
        CLK_FREQ    : integer := 50000000;  -- 50 MHz
        I2C_FREQ    : integer := 100000     -- 100 kHz
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Control
        start_txn   : in  bit;
        stop_txn    : in  bit;
        read_mode   : in  bit;  -- '1' = read, '0' = write
        ack_mode    : in  bit;  -- For read: '0' = ACK, '1' = NACK (last byte)
        -- Data
        slave_addr  : in  bit_vector(6 downto 0);
        data_in     : in  bit_vector(7 downto 0);
        data_out    : out bit_vector(7 downto 0);
        -- Status
        busy        : out bit;
        done        : out bit;
        ack_error   : out bit;
        -- I2C interface (directly instead of open-drain)
        scl_out     : out bit;
        scl_in      : in  bit;
        sda_out     : out bit;
        sda_in      : in  bit;
        scl_oe      : out bit;  -- Output enable for SCL
        sda_oe      : out bit   -- Output enable for SDA
    );
end I2CMaster;

architecture Behavioral of I2CMaster is
    constant CLK_DIV : integer := CLK_FREQ / (I2C_FREQ * 4);

    type state_type is (
        IDLE,
        START_1, START_2,
        SEND_BIT, SEND_BIT_HIGH, SEND_BIT_LOW,
        READ_BIT, READ_BIT_HIGH, READ_BIT_LOW,
        CHECK_ACK, CHECK_ACK_HIGH, CHECK_ACK_LOW,
        SEND_ACK, SEND_ACK_HIGH, SEND_ACK_LOW,
        STOP_1, STOP_2, STOP_3,
        REPEAT_START
    );
    signal state : state_type;

    signal clk_counter : integer range 0 to CLK_DIV;
    signal bit_counter : integer range 0 to 8;
    signal byte_counter : integer range 0 to 255;

    signal shift_reg : bit_vector(7 downto 0);
    signal scl_reg : bit;
    signal sda_reg : bit;

    signal is_addr_phase : bit;
    signal ack_received : bit;

begin
    i2c_proc: process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE;
            clk_counter <= 0;
            bit_counter <= 0;
            scl_reg <= '1';
            sda_reg <= '1';
            scl_oe <= '0';
            sda_oe <= '0';
            busy <= '0';
            done <= '0';
            ack_error <= '0';
            is_addr_phase <= '0';
            data_out <= (others => '0');
        elsif clk'event and clk = '1' then
            done <= '0';

            case state is
                when IDLE =>
                    scl_reg <= '1';
                    sda_reg <= '1';
                    scl_oe <= '0';
                    sda_oe <= '0';
                    busy <= '0';

                    if start_txn = '1' then
                        busy <= '1';
                        -- Load address + R/W bit
                        shift_reg <= slave_addr & read_mode;
                        is_addr_phase <= '1';
                        state <= START_1;
                        clk_counter <= 0;
                    end if;

                when START_1 =>
                    -- SDA goes low while SCL high
                    scl_oe <= '1';
                    sda_oe <= '1';
                    scl_reg <= '1';
                    sda_reg <= '1';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        state <= START_2;
                    end if;

                when START_2 =>
                    sda_reg <= '0';  -- Start condition

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        scl_reg <= '0';
                        bit_counter <= 7;
                        state <= SEND_BIT;
                    end if;

                when SEND_BIT =>
                    -- Output bit on SDA while SCL low
                    scl_reg <= '0';
                    sda_reg <= shift_reg(7);

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        state <= SEND_BIT_HIGH;
                    end if;

                when SEND_BIT_HIGH =>
                    scl_reg <= '1';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        state <= SEND_BIT_LOW;
                    end if;

                when SEND_BIT_LOW =>
                    scl_reg <= '0';
                    shift_reg <= shift_reg(6 downto 0) & '0';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        if bit_counter > 0 then
                            bit_counter <= bit_counter - 1;
                            state <= SEND_BIT;
                        else
                            state <= CHECK_ACK;
                        end if;
                    end if;

                when CHECK_ACK =>
                    -- Release SDA for ACK
                    sda_oe <= '0';
                    sda_reg <= '1';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        state <= CHECK_ACK_HIGH;
                    end if;

                when CHECK_ACK_HIGH =>
                    scl_reg <= '1';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        -- Sample ACK
                        ack_received <= not sda_in;  -- ACK = SDA low
                        clk_counter <= 0;
                        state <= CHECK_ACK_LOW;
                    end if;

                when CHECK_ACK_LOW =>
                    scl_reg <= '0';
                    sda_oe <= '1';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        if ack_received = '0' then
                            -- NACK received
                            ack_error <= '1';
                            state <= STOP_1;
                        elsif stop_txn = '1' then
                            state <= STOP_1;
                            done <= '1';
                        elsif is_addr_phase = '1' then
                            is_addr_phase <= '0';
                            if read_mode = '1' then
                                -- Start reading
                                bit_counter <= 7;
                                state <= READ_BIT;
                            else
                                -- Load write data
                                shift_reg <= data_in;
                                bit_counter <= 7;
                                state <= SEND_BIT;
                            end if;
                        else
                            -- Load next byte
                            shift_reg <= data_in;
                            bit_counter <= 7;
                            if read_mode = '1' then
                                state <= READ_BIT;
                            else
                                state <= SEND_BIT;
                            end if;
                        end if;
                    end if;

                when READ_BIT =>
                    sda_oe <= '0';  -- Release SDA for read
                    scl_reg <= '0';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        state <= READ_BIT_HIGH;
                    end if;

                when READ_BIT_HIGH =>
                    scl_reg <= '1';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        -- Sample data
                        shift_reg <= shift_reg(6 downto 0) & sda_in;
                        clk_counter <= 0;
                        state <= READ_BIT_LOW;
                    end if;

                when READ_BIT_LOW =>
                    scl_reg <= '0';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        if bit_counter > 0 then
                            bit_counter <= bit_counter - 1;
                            state <= READ_BIT;
                        else
                            data_out <= shift_reg;
                            state <= SEND_ACK;
                        end if;
                    end if;

                when SEND_ACK =>
                    sda_oe <= '1';
                    sda_reg <= ack_mode;  -- ACK = '0', NACK = '1'
                    scl_reg <= '0';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        state <= SEND_ACK_HIGH;
                    end if;

                when SEND_ACK_HIGH =>
                    scl_reg <= '1';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        state <= SEND_ACK_LOW;
                    end if;

                when SEND_ACK_LOW =>
                    scl_reg <= '0';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        done <= '1';
                        if ack_mode = '1' or stop_txn = '1' then
                            state <= STOP_1;
                        else
                            bit_counter <= 7;
                            state <= READ_BIT;
                        end if;
                    end if;

                when STOP_1 =>
                    sda_oe <= '1';
                    sda_reg <= '0';
                    scl_reg <= '0';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        state <= STOP_2;
                    end if;

                when STOP_2 =>
                    scl_reg <= '1';

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        clk_counter <= 0;
                        state <= STOP_3;
                    end if;

                when STOP_3 =>
                    sda_reg <= '1';  -- Stop condition

                    if clk_counter < CLK_DIV then
                        clk_counter <= clk_counter + 1;
                    else
                        state <= IDLE;
                    end if;

                when REPEAT_START =>
                    state <= START_1;

            end case;
        end if;
    end process;

    scl_out <= scl_reg;
    sda_out <= sda_reg;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity I2CMaster_TB is
end I2CMaster_TB;

architecture Test of I2CMaster_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_start, tb_stop, tb_read, tb_ack_mode : bit;
    signal tb_slave_addr : bit_vector(6 downto 0);
    signal tb_data_in, tb_data_out : bit_vector(7 downto 0);
    signal tb_busy, tb_done, tb_ack_error : bit;
    signal tb_scl_out, tb_scl_in, tb_sda_out, tb_sda_in : bit;
    signal tb_scl_oe, tb_sda_oe : bit;
begin
    UUT: entity work.I2CMaster
        generic map (CLK_FREQ => 1000000, I2C_FREQ => 100000)
        port map (
            clk => tb_clk, reset => tb_reset,
            start_txn => tb_start, stop_txn => tb_stop,
            read_mode => tb_read, ack_mode => tb_ack_mode,
            slave_addr => tb_slave_addr,
            data_in => tb_data_in, data_out => tb_data_out,
            busy => tb_busy, done => tb_done, ack_error => tb_ack_error,
            scl_out => tb_scl_out, scl_in => tb_scl_in,
            sda_out => tb_sda_out, sda_in => tb_sda_in,
            scl_oe => tb_scl_oe, sda_oe => tb_sda_oe
        );

    -- Pullups and slave ACK simulation
    tb_scl_in <= tb_scl_out when tb_scl_oe = '1' else '1';
    tb_sda_in <= '0';  -- Simulated slave ACK

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 500 ns;
        tb_clk <= '1'; wait for 500 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_start <= '0'; tb_stop <= '0';
        tb_read <= '0'; tb_ack_mode <= '0';
        tb_slave_addr <= "1010000";  -- 0x50
        tb_data_in <= (others => '0');
        wait for 10 us;

        tb_reset <= '0';
        wait for 10 us;

        -- Write transaction
        tb_data_in <= "11001100";
        tb_start <= '1';
        wait for 1 us;
        tb_start <= '0';
        tb_stop <= '1';

        wait until tb_done = '1';
        wait for 100 us;

        wait;
    end process;
end Test;
