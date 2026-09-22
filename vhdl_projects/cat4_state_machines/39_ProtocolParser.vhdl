-- ============================================================================
-- VHDL Project: ProtocolParser
-- Category: State Machines
-- Complexity: High
-- Description: Generic packet parser FSM for serial protocols
--              Handles framing, CRC checking, field extraction
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ProtocolParser is
    generic (
        MAX_PAYLOAD_SIZE : integer := 256;
        HEADER_SIZE      : integer := 4;
        CRC_SIZE         : integer := 2
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Input byte stream
        rx_data         : in  bit_vector(7 downto 0);
        rx_valid        : in  bit;
        -- Frame markers
        frame_start     : in  bit_vector(7 downto 0);  -- Start delimiter
        frame_end       : in  bit_vector(7 downto 0);  -- End delimiter
        escape_char     : in  bit_vector(7 downto 0);  -- Escape character
        -- Parsed output
        packet_type     : out bit_vector(7 downto 0);
        packet_length   : out bit_vector(15 downto 0);
        payload_byte    : out bit_vector(7 downto 0);
        payload_index   : out bit_vector(7 downto 0);
        payload_valid   : out bit;
        -- Status
        packet_start    : out bit;
        packet_complete : out bit;
        packet_error    : out bit;
        crc_valid       : out bit;
        state_out       : out bit_vector(3 downto 0)
    );
end ProtocolParser;

architecture Behavioral of ProtocolParser is
    -- Packet format:
    -- [START] [TYPE] [LEN_H] [LEN_L] [PAYLOAD...] [CRC_H] [CRC_L] [END]

    type state_type is (
        IDLE,
        WAIT_TYPE,
        WAIT_LEN_H,
        WAIT_LEN_L,
        RECEIVING_PAYLOAD,
        WAIT_CRC_H,
        WAIT_CRC_L,
        WAIT_END,
        PROCESS_ESCAPE,
        PACKET_DONE,
        PACKET_ERROR
    );

    signal state : state_type;
    signal return_state : state_type;

    -- Packet fields
    signal pkt_type : bit_vector(7 downto 0);
    signal pkt_len : bit_vector(15 downto 0);
    signal pkt_index : integer range 0 to MAX_PAYLOAD_SIZE;
    signal rx_crc : bit_vector(15 downto 0);

    -- CRC calculation
    signal calc_crc : bit_vector(15 downto 0);

    -- Escape handling
    signal escaped : bit;
    signal prev_byte : bit_vector(7 downto 0);

    function bytes_equal(a, b : bit_vector) return bit is
    begin
        for i in a'range loop
            if a(i) /= b(i) then
                return '0';
            end if;
        end loop;
        return '1';
    end function;

    -- CRC-16 CCITT update
    function crc16_update(crc : bit_vector; data : bit_vector) return bit_vector is
        variable result : bit_vector(15 downto 0);
        variable d : bit;
    begin
        result := crc;
        for i in 7 downto 0 loop
            d := data(i) xor result(15);
            result := result(14 downto 0) & '0';
            if d = '1' then
                result := result xor "0001000000100001";  -- x^16 + x^12 + x^5 + 1
            end if;
        end loop;
        return result;
    end function;

    function to_integer(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then
                result := result + power;
            end if;
            power := power * 2;
        end loop;
        return result;
    end function;

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
    parser_proc: process(clk, reset)
        variable actual_data : bit_vector(7 downto 0);
    begin
        if reset = '1' then
            state <= IDLE;
            return_state <= IDLE;
            pkt_type <= (others => '0');
            pkt_len <= (others => '0');
            pkt_index <= 0;
            rx_crc <= (others => '0');
            calc_crc <= (others => '1');  -- Initial CRC value
            escaped <= '0';
            prev_byte <= (others => '0');
        elsif clk'event and clk = '1' then
            -- Default outputs
            payload_valid <= '0';
            packet_start <= '0';
            packet_complete <= '0';
            packet_error <= '0';

            if rx_valid = '1' then
                -- Check for escape character
                if bytes_equal(rx_data, escape_char) = '1' and escaped = '0' then
                    escaped <= '1';
                    -- Stay in current state
                else
                    -- Process actual data (unescape if needed)
                    if escaped = '1' then
                        actual_data := rx_data xor "00100000";  -- XOR with 0x20
                        escaped <= '0';
                    else
                        actual_data := rx_data;
                    end if;

                    case state is
                        when IDLE =>
                            if bytes_equal(actual_data, frame_start) = '1' then
                                state <= WAIT_TYPE;
                                packet_start <= '1';
                                calc_crc <= (others => '1');  -- Reset CRC
                            end if;

                        when WAIT_TYPE =>
                            pkt_type <= actual_data;
                            calc_crc <= crc16_update(calc_crc, actual_data);
                            state <= WAIT_LEN_H;

                        when WAIT_LEN_H =>
                            pkt_len(15 downto 8) <= actual_data;
                            calc_crc <= crc16_update(calc_crc, actual_data);
                            state <= WAIT_LEN_L;

                        when WAIT_LEN_L =>
                            pkt_len(7 downto 0) <= actual_data;
                            calc_crc <= crc16_update(calc_crc, actual_data);
                            pkt_index <= 0;
                            -- Check length validity
                            if to_integer(pkt_len(15 downto 8) & actual_data) > MAX_PAYLOAD_SIZE then
                                state <= PACKET_ERROR;
                            elsif to_integer(pkt_len(15 downto 8) & actual_data) = 0 then
                                state <= WAIT_CRC_H;
                            else
                                state <= RECEIVING_PAYLOAD;
                            end if;

                        when RECEIVING_PAYLOAD =>
                            payload_byte <= actual_data;
                            payload_index <= to_bv8(pkt_index);
                            payload_valid <= '1';
                            calc_crc <= crc16_update(calc_crc, actual_data);
                            pkt_index <= pkt_index + 1;

                            if pkt_index >= to_integer(pkt_len) - 1 then
                                state <= WAIT_CRC_H;
                            end if;

                        when WAIT_CRC_H =>
                            rx_crc(15 downto 8) <= actual_data;
                            state <= WAIT_CRC_L;

                        when WAIT_CRC_L =>
                            rx_crc(7 downto 0) <= actual_data;
                            state <= WAIT_END;

                        when WAIT_END =>
                            if bytes_equal(actual_data, frame_end) = '1' then
                                state <= PACKET_DONE;
                            else
                                state <= PACKET_ERROR;
                            end if;

                        when PROCESS_ESCAPE =>
                            state <= return_state;

                        when PACKET_DONE =>
                            packet_complete <= '1';
                            state <= IDLE;

                        when PACKET_ERROR =>
                            packet_error <= '1';
                            -- Look for next start delimiter
                            if bytes_equal(actual_data, frame_start) = '1' then
                                state <= WAIT_TYPE;
                                packet_start <= '1';
                                calc_crc <= (others => '1');
                            end if;

                    end case;
                end if;
            end if;
        end if;
    end process;

    -- Output assignments
    packet_type <= pkt_type;
    packet_length <= pkt_len;
    crc_valid <= '1' when calc_crc = rx_crc else '0';

    with state select state_out <=
        "0000" when IDLE,
        "0001" when WAIT_TYPE,
        "0010" when WAIT_LEN_H,
        "0011" when WAIT_LEN_L,
        "0100" when RECEIVING_PAYLOAD,
        "0101" when WAIT_CRC_H,
        "0110" when WAIT_CRC_L,
        "0111" when WAIT_END,
        "1000" when PROCESS_ESCAPE,
        "1001" when PACKET_DONE,
        "1111" when PACKET_ERROR;

end Behavioral;

-- ============================================================================
-- Command Interpreter (uses parsed packets)
-- ============================================================================
entity CommandInterpreter is
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- From parser
        packet_type     : in  bit_vector(7 downto 0);
        packet_length   : in  bit_vector(15 downto 0);
        payload_byte    : in  bit_vector(7 downto 0);
        payload_valid   : in  bit;
        packet_complete : in  bit;
        -- Command outputs
        cmd_read        : out bit;
        cmd_write       : out bit;
        cmd_execute     : out bit;
        cmd_reset       : out bit;
        cmd_address     : out bit_vector(15 downto 0);
        cmd_data        : out bit_vector(31 downto 0);
        cmd_valid       : out bit;
        cmd_error       : out bit
    );
end CommandInterpreter;

architecture Behavioral of CommandInterpreter is
    type state_type is (IDLE, GET_ADDR_H, GET_ADDR_L, GET_DATA, EXECUTE);
    signal state : state_type;

    signal addr_reg : bit_vector(15 downto 0);
    signal data_reg : bit_vector(31 downto 0);
    signal cmd_type : bit_vector(7 downto 0);
    signal byte_count : integer range 0 to 7;

    -- Command types
    constant CMD_READ    : bit_vector(7 downto 0) := "00000001";
    constant CMD_WRITE   : bit_vector(7 downto 0) := "00000010";
    constant CMD_EXEC    : bit_vector(7 downto 0) := "00000100";
    constant CMD_RST     : bit_vector(7 downto 0) := "00001000";

begin
    process(clk, reset)
    begin
        if reset = '1' then
            state <= IDLE;
            addr_reg <= (others => '0');
            data_reg <= (others => '0');
            cmd_type <= (others => '0');
            byte_count <= 0;
            cmd_valid <= '0';
            cmd_error <= '0';
        elsif clk'event and clk = '1' then
            cmd_valid <= '0';
            cmd_error <= '0';

            case state is
                when IDLE =>
                    if packet_complete = '1' then
                        cmd_type <= packet_type;
                        byte_count <= 0;
                        state <= GET_ADDR_H;
                    end if;

                when GET_ADDR_H =>
                    if payload_valid = '1' then
                        addr_reg(15 downto 8) <= payload_byte;
                        state <= GET_ADDR_L;
                    end if;

                when GET_ADDR_L =>
                    if payload_valid = '1' then
                        addr_reg(7 downto 0) <= payload_byte;
                        if cmd_type = CMD_READ then
                            state <= EXECUTE;
                        else
                            state <= GET_DATA;
                            byte_count <= 0;
                        end if;
                    end if;

                when GET_DATA =>
                    if payload_valid = '1' then
                        case byte_count is
                            when 0 => data_reg(31 downto 24) <= payload_byte;
                            when 1 => data_reg(23 downto 16) <= payload_byte;
                            when 2 => data_reg(15 downto 8) <= payload_byte;
                            when 3 => data_reg(7 downto 0) <= payload_byte;
                            when others => null;
                        end case;
                        byte_count <= byte_count + 1;
                        if byte_count >= 3 then
                            state <= EXECUTE;
                        end if;
                    end if;

                when EXECUTE =>
                    cmd_valid <= '1';
                    state <= IDLE;

            end case;
        end if;
    end process;

    cmd_read <= '1' when cmd_type = CMD_READ and state = EXECUTE else '0';
    cmd_write <= '1' when cmd_type = CMD_WRITE and state = EXECUTE else '0';
    cmd_execute <= '1' when cmd_type = CMD_EXEC and state = EXECUTE else '0';
    cmd_reset <= '1' when cmd_type = CMD_RST and state = EXECUTE else '0';
    cmd_address <= addr_reg;
    cmd_data <= data_reg;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity ProtocolParser_TB is
end ProtocolParser_TB;

architecture Test of ProtocolParser_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_rx_data : bit_vector(7 downto 0);
    signal tb_rx_valid : bit;
    signal tb_frame_start, tb_frame_end, tb_escape : bit_vector(7 downto 0);
    signal tb_pkt_type : bit_vector(7 downto 0);
    signal tb_pkt_len : bit_vector(15 downto 0);
    signal tb_payload : bit_vector(7 downto 0);
    signal tb_payload_idx : bit_vector(7 downto 0);
    signal tb_payload_valid : bit;
    signal tb_pkt_start, tb_pkt_complete, tb_pkt_error : bit;
    signal tb_crc_valid : bit;
    signal tb_state : bit_vector(3 downto 0);
begin
    UUT: entity work.ProtocolParser
        generic map (MAX_PAYLOAD_SIZE => 256, HEADER_SIZE => 4, CRC_SIZE => 2)
        port map (
            clk             => tb_clk,
            reset           => tb_reset,
            rx_data         => tb_rx_data,
            rx_valid        => tb_rx_valid,
            frame_start     => tb_frame_start,
            frame_end       => tb_frame_end,
            escape_char     => tb_escape,
            packet_type     => tb_pkt_type,
            packet_length   => tb_pkt_len,
            payload_byte    => tb_payload,
            payload_index   => tb_payload_idx,
            payload_valid   => tb_payload_valid,
            packet_start    => tb_pkt_start,
            packet_complete => tb_pkt_complete,
            packet_error    => tb_pkt_error,
            crc_valid       => tb_crc_valid,
            state_out       => tb_state
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1';
        tb_rx_data <= (others => '0');
        tb_rx_valid <= '0';
        tb_frame_start <= "01111110";  -- 0x7E
        tb_frame_end <= "01111110";    -- 0x7E
        tb_escape <= "01111101";       -- 0x7D
        wait for 30 ns;

        tb_reset <= '0';

        -- Send a simple packet: [START] [TYPE=0x01] [LEN_H=0x00] [LEN_L=0x02] [DATA1] [DATA2] [CRC_H] [CRC_L] [END]

        -- Start delimiter
        tb_rx_data <= "01111110"; tb_rx_valid <= '1'; wait for 10 ns; tb_rx_valid <= '0'; wait for 10 ns;

        -- Type
        tb_rx_data <= "00000001"; tb_rx_valid <= '1'; wait for 10 ns; tb_rx_valid <= '0'; wait for 10 ns;

        -- Length high
        tb_rx_data <= "00000000"; tb_rx_valid <= '1'; wait for 10 ns; tb_rx_valid <= '0'; wait for 10 ns;

        -- Length low
        tb_rx_data <= "00000010"; tb_rx_valid <= '1'; wait for 10 ns; tb_rx_valid <= '0'; wait for 10 ns;

        -- Payload byte 1
        tb_rx_data <= "10101010"; tb_rx_valid <= '1'; wait for 10 ns; tb_rx_valid <= '0'; wait for 10 ns;

        -- Payload byte 2
        tb_rx_data <= "01010101"; tb_rx_valid <= '1'; wait for 10 ns; tb_rx_valid <= '0'; wait for 10 ns;

        -- CRC (dummy)
        tb_rx_data <= "00000000"; tb_rx_valid <= '1'; wait for 10 ns; tb_rx_valid <= '0'; wait for 10 ns;
        tb_rx_data <= "00000000"; tb_rx_valid <= '1'; wait for 10 ns; tb_rx_valid <= '0'; wait for 10 ns;

        -- End delimiter
        tb_rx_data <= "01111110"; tb_rx_valid <= '1'; wait for 10 ns; tb_rx_valid <= '0'; wait for 10 ns;

        wait for 100 ns;
        wait;
    end process;
end Test;
