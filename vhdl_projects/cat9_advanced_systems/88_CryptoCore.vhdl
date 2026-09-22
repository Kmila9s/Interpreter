-- ============================================================================
-- VHDL Project: CryptoCore
-- Category: Advanced Systems
-- Complexity: Very High
-- Description: Basic cryptographic coprocessor with AES-like operations
--              S-box, MixColumns, KeyExpansion
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity CryptoCore is
    generic (
        KEY_SIZE   : integer := 128;
        BLOCK_SIZE : integer := 128
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        -- Control
        start      : in  bit;
        encrypt    : in  bit;  -- '1' = encrypt, '0' = decrypt
        -- Key input
        key        : in  bit_vector(KEY_SIZE-1 downto 0);
        key_valid  : in  bit;
        -- Data interface
        data_in    : in  bit_vector(BLOCK_SIZE-1 downto 0);
        data_out   : out bit_vector(BLOCK_SIZE-1 downto 0);
        -- Status
        busy       : out bit;
        done       : out bit
    );
end CryptoCore;

architecture Behavioral of CryptoCore is
    type state_type is (IDLE, KEY_EXPAND, INIT_ROUND, MAIN_ROUNDS, FINAL_ROUND, COMPLETE);
    signal state : state_type;

    -- State matrix (4x4 bytes)
    type state_matrix is array (0 to 3, 0 to 3) of bit_vector(7 downto 0);
    signal state_reg : state_matrix;

    -- Round keys
    type round_keys is array (0 to 10) of bit_vector(127 downto 0);
    signal rkeys : round_keys;

    signal round_num : integer range 0 to 10;
    signal key_ready : bit;

    -- AES S-box (substitution box) - first 256 values
    type sbox_type is array (0 to 255) of bit_vector(7 downto 0);
    constant SBOX : sbox_type := (
        x"63", x"7c", x"77", x"7b", x"f2", x"6b", x"6f", x"c5",
        x"30", x"01", x"67", x"2b", x"fe", x"d7", x"ab", x"76",
        x"ca", x"82", x"c9", x"7d", x"fa", x"59", x"47", x"f0",
        x"ad", x"d4", x"a2", x"af", x"9c", x"a4", x"72", x"c0",
        x"b7", x"fd", x"93", x"26", x"36", x"3f", x"f7", x"cc",
        x"34", x"a5", x"e5", x"f1", x"71", x"d8", x"31", x"15",
        x"04", x"c7", x"23", x"c3", x"18", x"96", x"05", x"9a",
        x"07", x"12", x"80", x"e2", x"eb", x"27", x"b2", x"75",
        x"09", x"83", x"2c", x"1a", x"1b", x"6e", x"5a", x"a0",
        x"52", x"3b", x"d6", x"b3", x"29", x"e3", x"2f", x"84",
        x"53", x"d1", x"00", x"ed", x"20", x"fc", x"b1", x"5b",
        x"6a", x"cb", x"be", x"39", x"4a", x"4c", x"58", x"cf",
        x"d0", x"ef", x"aa", x"fb", x"43", x"4d", x"33", x"85",
        x"45", x"f9", x"02", x"7f", x"50", x"3c", x"9f", x"a8",
        x"51", x"a3", x"40", x"8f", x"92", x"9d", x"38", x"f5",
        x"bc", x"b6", x"da", x"21", x"10", x"ff", x"f3", x"d2",
        x"cd", x"0c", x"13", x"ec", x"5f", x"97", x"44", x"17",
        x"c4", x"a7", x"7e", x"3d", x"64", x"5d", x"19", x"73",
        x"60", x"81", x"4f", x"dc", x"22", x"2a", x"90", x"88",
        x"46", x"ee", x"b8", x"14", x"de", x"5e", x"0b", x"db",
        x"e0", x"32", x"3a", x"0a", x"49", x"06", x"24", x"5c",
        x"c2", x"d3", x"ac", x"62", x"91", x"95", x"e4", x"79",
        x"e7", x"c8", x"37", x"6d", x"8d", x"d5", x"4e", x"a9",
        x"6c", x"56", x"f4", x"ea", x"65", x"7a", x"ae", x"08",
        x"ba", x"78", x"25", x"2e", x"1c", x"a6", x"b4", x"c6",
        x"e8", x"dd", x"74", x"1f", x"4b", x"bd", x"8b", x"8a",
        x"70", x"3e", x"b5", x"66", x"48", x"03", x"f6", x"0e",
        x"61", x"35", x"57", x"b9", x"86", x"c1", x"1d", x"9e",
        x"e1", x"f8", x"98", x"11", x"69", x"d9", x"8e", x"94",
        x"9b", x"1e", x"87", x"e9", x"ce", x"55", x"28", x"df",
        x"8c", x"a1", x"89", x"0d", x"bf", x"e6", x"42", x"68",
        x"41", x"99", x"2d", x"0f", x"b0", x"54", x"bb", x"16"
    );

    -- Round constants
    type rcon_type is array (0 to 9) of bit_vector(7 downto 0);
    constant RCON : rcon_type := (
        x"01", x"02", x"04", x"08", x"10", x"20", x"40", x"80", x"1b", x"36"
    );

    function to_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then result := result + power; end if;
            power := power * 2;
        end loop;
        return result;
    end function;

    function sub_byte(b : bit_vector) return bit_vector is
    begin
        return SBOX(to_int(b));
    end function;

    function xtime(b : bit_vector) return bit_vector is
        variable result : bit_vector(7 downto 0);
        variable msb : bit;
    begin
        msb := b(7);
        result := b(6 downto 0) & '0';
        if msb = '1' then
            result := result xor x"1b";
        end if;
        return result;
    end function;

begin
    crypto_proc: process(clk, reset)
        variable temp : bit_vector(7 downto 0);
        variable t0, t1, t2, t3 : bit_vector(7 downto 0);
        variable word : bit_vector(31 downto 0);
        variable prev_word : bit_vector(31 downto 0);
    begin
        if reset = '1' then
            state <= IDLE;
            round_num <= 0;
            key_ready <= '0';
            busy <= '0';
            done <= '0';
            data_out <= (others => '0');
            for i in 0 to 3 loop
                for j in 0 to 3 loop
                    state_reg(i, j) <= (others => '0');
                end loop;
            end loop;
        elsif clk'event and clk = '1' then
            done <= '0';

            case state is
                when IDLE =>
                    busy <= '0';
                    if key_valid = '1' then
                        -- Store initial round key
                        rkeys(0) <= key;
                        key_ready <= '0';
                        state <= KEY_EXPAND;
                        round_num <= 1;
                    elsif start = '1' and key_ready = '1' then
                        busy <= '1';
                        -- Load input data into state
                        for i in 0 to 3 loop
                            for j in 0 to 3 loop
                                state_reg(j, i) <= data_in((15-i*4-j)*8+7 downto (15-i*4-j)*8);
                            end loop;
                        end loop;
                        state <= INIT_ROUND;
                        round_num <= 0;
                    end if;

                when KEY_EXPAND =>
                    -- Simplified key expansion
                    prev_word := rkeys(round_num-1)(31 downto 0);

                    -- RotWord
                    word := prev_word(23 downto 0) & prev_word(31 downto 24);

                    -- SubWord
                    word := sub_byte(word(31 downto 24)) &
                            sub_byte(word(23 downto 16)) &
                            sub_byte(word(15 downto 8)) &
                            sub_byte(word(7 downto 0));

                    -- XOR with Rcon
                    word(31 downto 24) := word(31 downto 24) xor RCON(round_num-1);

                    -- Generate round key
                    rkeys(round_num)(127 downto 96) <=
                        rkeys(round_num-1)(127 downto 96) xor word;
                    rkeys(round_num)(95 downto 64) <=
                        rkeys(round_num-1)(95 downto 64) xor
                        (rkeys(round_num-1)(127 downto 96) xor word);
                    rkeys(round_num)(63 downto 32) <=
                        rkeys(round_num-1)(63 downto 32) xor
                        rkeys(round_num-1)(95 downto 64) xor
                        (rkeys(round_num-1)(127 downto 96) xor word);
                    rkeys(round_num)(31 downto 0) <=
                        rkeys(round_num-1)(31 downto 0) xor
                        rkeys(round_num-1)(63 downto 32) xor
                        rkeys(round_num-1)(95 downto 64) xor
                        (rkeys(round_num-1)(127 downto 96) xor word);

                    if round_num = 10 then
                        key_ready <= '1';
                        state <= IDLE;
                    else
                        round_num <= round_num + 1;
                    end if;

                when INIT_ROUND =>
                    -- AddRoundKey with first round key
                    for i in 0 to 3 loop
                        for j in 0 to 3 loop
                            state_reg(j, i) <= state_reg(j, i) xor
                                rkeys(0)((15-i*4-j)*8+7 downto (15-i*4-j)*8);
                        end loop;
                    end loop;
                    round_num <= 1;
                    state <= MAIN_ROUNDS;

                when MAIN_ROUNDS =>
                    -- SubBytes
                    for i in 0 to 3 loop
                        for j in 0 to 3 loop
                            state_reg(i, j) <= sub_byte(state_reg(i, j));
                        end loop;
                    end loop;

                    -- ShiftRows
                    -- Row 1: shift left by 1
                    temp := state_reg(1, 0);
                    state_reg(1, 0) <= state_reg(1, 1);
                    state_reg(1, 1) <= state_reg(1, 2);
                    state_reg(1, 2) <= state_reg(1, 3);
                    state_reg(1, 3) <= temp;

                    -- Row 2: shift left by 2
                    temp := state_reg(2, 0);
                    state_reg(2, 0) <= state_reg(2, 2);
                    state_reg(2, 2) <= temp;
                    temp := state_reg(2, 1);
                    state_reg(2, 1) <= state_reg(2, 3);
                    state_reg(2, 3) <= temp;

                    -- Row 3: shift left by 3
                    temp := state_reg(3, 3);
                    state_reg(3, 3) <= state_reg(3, 2);
                    state_reg(3, 2) <= state_reg(3, 1);
                    state_reg(3, 1) <= state_reg(3, 0);
                    state_reg(3, 0) <= temp;

                    -- MixColumns (skip for round 10)
                    if round_num < 10 then
                        for i in 0 to 3 loop
                            t0 := state_reg(0, i);
                            t1 := state_reg(1, i);
                            t2 := state_reg(2, i);
                            t3 := state_reg(3, i);

                            state_reg(0, i) <= xtime(t0) xor xtime(t1) xor t1 xor t2 xor t3;
                            state_reg(1, i) <= t0 xor xtime(t1) xor xtime(t2) xor t2 xor t3;
                            state_reg(2, i) <= t0 xor t1 xor xtime(t2) xor xtime(t3) xor t3;
                            state_reg(3, i) <= xtime(t0) xor t0 xor t1 xor t2 xor xtime(t3);
                        end loop;
                    end if;

                    -- AddRoundKey
                    for i in 0 to 3 loop
                        for j in 0 to 3 loop
                            state_reg(j, i) <= state_reg(j, i) xor
                                rkeys(round_num)((15-i*4-j)*8+7 downto (15-i*4-j)*8);
                        end loop;
                    end loop;

                    if round_num = 10 then
                        state <= COMPLETE;
                    else
                        round_num <= round_num + 1;
                    end if;

                when FINAL_ROUND =>
                    state <= COMPLETE;

                when COMPLETE =>
                    -- Output result
                    for i in 0 to 3 loop
                        for j in 0 to 3 loop
                            data_out((15-i*4-j)*8+7 downto (15-i*4-j)*8) <= state_reg(j, i);
                        end loop;
                    end loop;
                    done <= '1';
                    busy <= '0';
                    state <= IDLE;
            end case;
        end if;
    end process;
end Behavioral;
