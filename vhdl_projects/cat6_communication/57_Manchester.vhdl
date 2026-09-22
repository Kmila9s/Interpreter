-- ============================================================================
-- VHDL Project: Manchester
-- Category: Communication Protocols
-- Complexity: Medium
-- Description: Manchester encoder/decoder for self-clocking communication
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ManchesterEncoder is
    generic (CLK_DIV : integer := 8);
    port (
        clk, reset : in bit;
        data_in : in bit_vector(7 downto 0);
        start : in bit;
        busy, done : out bit;
        manchester_out : out bit
    );
end ManchesterEncoder;

architecture Behavioral of ManchesterEncoder is
    type state_type is (IDLE, PREAMBLE, DATA_FIRST, DATA_SECOND);
    signal state : state_type;
    signal clk_cnt : integer range 0 to CLK_DIV-1;
    signal bit_cnt : integer range 0 to 7;
    signal data_reg : bit_vector(7 downto 0);
    signal half_bit : bit;
begin
    process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE; manchester_out <= '1'; busy <= '0'; done <= '0';
        elsif clk'event and clk = '1' then
            done <= '0';
            if clk_cnt < CLK_DIV-1 then
                clk_cnt <= clk_cnt + 1;
            else
                clk_cnt <= 0;
                half_bit <= not half_bit;
                case state is
                    when IDLE =>
                        manchester_out <= '1'; busy <= '0';
                        if start = '1' then
                            data_reg <= data_in; bit_cnt <= 0; half_bit <= '0';
                            busy <= '1'; state <= DATA_FIRST;
                        end if;
                    when PREAMBLE =>
                        manchester_out <= half_bit;
                        if half_bit = '1' then state <= DATA_FIRST; end if;
                    when DATA_FIRST =>
                        if data_reg(7-bit_cnt) = '0' then
                            manchester_out <= '0';
                        else
                            manchester_out <= '1';
                        end if;
                        state <= DATA_SECOND;
                    when DATA_SECOND =>
                        if data_reg(7-bit_cnt) = '0' then
                            manchester_out <= '1';
                        else
                            manchester_out <= '0';
                        end if;
                        if bit_cnt = 7 then
                            done <= '1'; state <= IDLE;
                        else
                            bit_cnt <= bit_cnt + 1; state <= DATA_FIRST;
                        end if;
                end case;
            end if;
        end if;
    end process;
end Behavioral;

entity ManchesterDecoder is
    generic (CLK_DIV : integer := 8);
    port (
        clk, reset : in bit;
        manchester_in : in bit;
        data_out : out bit_vector(7 downto 0);
        valid, error : out bit
    );
end ManchesterDecoder;

architecture Behavioral of ManchesterDecoder is
    signal in_sync : bit_vector(2 downto 0);
    signal edge_detect : bit;
    signal sample_cnt : integer range 0 to CLK_DIV*3;
    signal bit_cnt : integer range 0 to 8;
    signal data_reg : bit_vector(7 downto 0);
    signal in_frame : bit;
begin
    process(clk, reset)
    begin
        if reset = '1' then
            in_sync <= "111"; in_frame <= '0'; valid <= '0';
        elsif clk'event and clk = '1' then
            in_sync <= in_sync(1 downto 0) & manchester_in;
            edge_detect <= in_sync(2) xor in_sync(1);
            valid <= '0'; error <= '0';

            if in_frame = '0' then
                if edge_detect = '1' then
                    in_frame <= '1'; sample_cnt <= 0; bit_cnt <= 0;
                end if;
            else
                sample_cnt <= sample_cnt + 1;
                if sample_cnt = (CLK_DIV * 3) / 2 then
                    data_reg <= data_reg(6 downto 0) & in_sync(2);
                    sample_cnt <= 0;
                    bit_cnt <= bit_cnt + 1;
                    if bit_cnt = 7 then
                        data_out <= data_reg(6 downto 0) & in_sync(2);
                        valid <= '1'; in_frame <= '0';
                    end if;
                end if;
                if edge_detect = '1' and sample_cnt < CLK_DIV/2 then
                    sample_cnt <= 0;
                end if;
            end if;
        end if;
    end process;
end Behavioral;
