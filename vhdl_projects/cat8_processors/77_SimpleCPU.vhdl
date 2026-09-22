-- ============================================================================
-- VHDL Project: SimpleCPU
-- Category: Processors
-- Complexity: Very High
-- Description: Simple 8-bit CPU with basic instruction set
--              Accumulator-based architecture, Harvard memory model
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity SimpleCPU is
    generic (
        DATA_WIDTH : integer := 8;
        ADDR_WIDTH : integer := 8
    );
    port (
        clk          : in  bit;
        reset        : in  bit;
        -- Program memory interface
        prog_addr    : out bit_vector(ADDR_WIDTH-1 downto 0);
        prog_data    : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Data memory interface
        data_addr    : out bit_vector(ADDR_WIDTH-1 downto 0);
        data_in      : in  bit_vector(DATA_WIDTH-1 downto 0);
        data_out     : out bit_vector(DATA_WIDTH-1 downto 0);
        data_we      : out bit;
        data_re      : out bit;
        -- Status
        halted       : out bit;
        -- Debug
        debug_acc    : out bit_vector(DATA_WIDTH-1 downto 0);
        debug_pc     : out bit_vector(ADDR_WIDTH-1 downto 0);
        debug_state  : out bit_vector(2 downto 0)
    );
end SimpleCPU;

architecture Behavioral of SimpleCPU is
    -- Instructions (4-bit opcode + 4-bit operand)
    constant OP_NOP   : bit_vector(3 downto 0) := "0000";
    constant OP_LDA   : bit_vector(3 downto 0) := "0001";  -- Load immediate to A
    constant OP_LDM   : bit_vector(3 downto 0) := "0010";  -- Load from memory to A
    constant OP_STA   : bit_vector(3 downto 0) := "0011";  -- Store A to memory
    constant OP_ADD   : bit_vector(3 downto 0) := "0100";  -- Add memory to A
    constant OP_SUB   : bit_vector(3 downto 0) := "0101";  -- Subtract memory from A
    constant OP_AND_OP : bit_vector(3 downto 0) := "0110"; -- AND memory with A
    constant OP_OR_OP : bit_vector(3 downto 0) := "0111";  -- OR memory with A
    constant OP_JMP   : bit_vector(3 downto 0) := "1000";  -- Jump
    constant OP_JZ    : bit_vector(3 downto 0) := "1001";  -- Jump if zero
    constant OP_JNZ   : bit_vector(3 downto 0) := "1010";  -- Jump if not zero
    constant OP_JC    : bit_vector(3 downto 0) := "1011";  -- Jump if carry
    constant OP_INC   : bit_vector(3 downto 0) := "1100";  -- Increment A
    constant OP_DEC   : bit_vector(3 downto 0) := "1101";  -- Decrement A
    constant OP_NOT_OP : bit_vector(3 downto 0) := "1110"; -- NOT A
    constant OP_HLT   : bit_vector(3 downto 0) := "1111";  -- Halt

    type state_type is (FETCH, DECODE, EXECUTE, MEMORY, WRITEBACK, HALT_STATE);
    signal state : state_type;

    -- Registers
    signal pc : bit_vector(ADDR_WIDTH-1 downto 0);
    signal ir : bit_vector(DATA_WIDTH-1 downto 0);
    signal acc : bit_vector(DATA_WIDTH-1 downto 0);
    signal mar : bit_vector(ADDR_WIDTH-1 downto 0);

    -- Flags
    signal zero_flag : bit;
    signal carry_flag : bit;

    -- Internal signals
    signal opcode : bit_vector(3 downto 0);
    signal operand : bit_vector(3 downto 0);

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

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer := val;
    begin
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    opcode <= ir(7 downto 4);
    operand <= ir(3 downto 0);

    process(clk, reset)
        variable alu_result : integer;
        variable sum : integer;
    begin
        if reset = '1' then
            state <= FETCH;
            pc <= (others => '0');
            ir <= (others => '0');
            acc <= (others => '0');
            mar <= (others => '0');
            zero_flag <= '1';
            carry_flag <= '0';
            data_we <= '0';
            data_re <= '0';
            halted <= '0';
        elsif clk'event and clk = '1' then
            data_we <= '0';
            data_re <= '0';

            case state is
                when FETCH =>
                    ir <= prog_data;
                    pc <= to_bv(to_int(pc) + 1, ADDR_WIDTH);
                    state <= DECODE;

                when DECODE =>
                    case opcode is
                        when OP_NOP =>
                            state <= FETCH;

                        when OP_LDA =>
                            -- Load immediate (operand is the value)
                            acc <= "0000" & operand;
                            state <= WRITEBACK;

                        when OP_LDM =>
                            -- Load from memory - need to read address
                            mar <= "0000" & operand;
                            data_re <= '1';
                            state <= MEMORY;

                        when OP_STA =>
                            -- Store to memory
                            mar <= "0000" & operand;
                            data_we <= '1';
                            state <= MEMORY;

                        when OP_ADD | OP_SUB | OP_AND_OP | OP_OR_OP =>
                            -- ALU operations need memory operand
                            mar <= "0000" & operand;
                            data_re <= '1';
                            state <= MEMORY;

                        when OP_JMP =>
                            pc <= "0000" & operand;
                            state <= FETCH;

                        when OP_JZ =>
                            if zero_flag = '1' then
                                pc <= "0000" & operand;
                            end if;
                            state <= FETCH;

                        when OP_JNZ =>
                            if zero_flag = '0' then
                                pc <= "0000" & operand;
                            end if;
                            state <= FETCH;

                        when OP_JC =>
                            if carry_flag = '1' then
                                pc <= "0000" & operand;
                            end if;
                            state <= FETCH;

                        when OP_INC =>
                            sum := to_int(acc) + 1;
                            if sum > 255 then
                                carry_flag <= '1';
                                sum := sum - 256;
                            else
                                carry_flag <= '0';
                            end if;
                            acc <= to_bv(sum, DATA_WIDTH);
                            state <= WRITEBACK;

                        when OP_DEC =>
                            sum := to_int(acc) - 1;
                            if sum < 0 then
                                carry_flag <= '1';
                                sum := sum + 256;
                            else
                                carry_flag <= '0';
                            end if;
                            acc <= to_bv(sum, DATA_WIDTH);
                            state <= WRITEBACK;

                        when OP_NOT_OP =>
                            for i in 0 to DATA_WIDTH-1 loop
                                acc(i) <= not acc(i);
                            end loop;
                            state <= WRITEBACK;

                        when OP_HLT =>
                            state <= HALT_STATE;

                        when others =>
                            state <= FETCH;
                    end case;

                when MEMORY =>
                    state <= EXECUTE;

                when EXECUTE =>
                    case opcode is
                        when OP_LDM =>
                            acc <= data_in;
                            state <= WRITEBACK;

                        when OP_STA =>
                            state <= FETCH;

                        when OP_ADD =>
                            sum := to_int(acc) + to_int(data_in);
                            if sum > 255 then
                                carry_flag <= '1';
                                sum := sum - 256;
                            else
                                carry_flag <= '0';
                            end if;
                            acc <= to_bv(sum, DATA_WIDTH);
                            state <= WRITEBACK;

                        when OP_SUB =>
                            sum := to_int(acc) - to_int(data_in);
                            if sum < 0 then
                                carry_flag <= '1';
                                sum := sum + 256;
                            else
                                carry_flag <= '0';
                            end if;
                            acc <= to_bv(sum, DATA_WIDTH);
                            state <= WRITEBACK;

                        when OP_AND_OP =>
                            for i in 0 to DATA_WIDTH-1 loop
                                acc(i) <= acc(i) and data_in(i);
                            end loop;
                            state <= WRITEBACK;

                        when OP_OR_OP =>
                            for i in 0 to DATA_WIDTH-1 loop
                                acc(i) <= acc(i) or data_in(i);
                            end loop;
                            state <= WRITEBACK;

                        when others =>
                            state <= FETCH;
                    end case;

                when WRITEBACK =>
                    -- Update zero flag
                    if acc = (acc'range => '0') then
                        zero_flag <= '1';
                    else
                        zero_flag <= '0';
                    end if;
                    state <= FETCH;

                when HALT_STATE =>
                    halted <= '1';
                    -- Stay halted
            end case;
        end if;
    end process;

    prog_addr <= pc;
    data_addr <= mar;
    data_out <= acc;

    -- Debug outputs
    debug_acc <= acc;
    debug_pc <= pc;
    debug_state <= "000" when state = FETCH else
                   "001" when state = DECODE else
                   "010" when state = EXECUTE else
                   "011" when state = MEMORY else
                   "100" when state = WRITEBACK else
                   "111";  -- HALT
end Behavioral;

-- ============================================================================
-- Simple Test Program ROM
-- ============================================================================
entity TestProgramROM is
    port (
        addr : in  bit_vector(7 downto 0);
        data : out bit_vector(7 downto 0)
    );
end TestProgramROM;

architecture Behavioral of TestProgramROM is
    type rom_type is array (0 to 255) of bit_vector(7 downto 0);

    -- Simple test program: count from 0 to 10
    constant ROM : rom_type := (
        0  => "00010101",  -- LDA 5      ; Load 5 into accumulator
        1  => "00110000",  -- STA 0      ; Store to address 0
        2  => "00010011",  -- LDA 3      ; Load 3
        3  => "01000000",  -- ADD 0      ; Add value at address 0 (5+3=8)
        4  => "00110001",  -- STA 1      ; Store result to address 1
        5  => "11000000",  -- INC        ; Increment (8+1=9)
        6  => "00110010",  -- STA 2      ; Store to address 2
        7  => "11110000",  -- HLT        ; Halt
        others => "00000000"  -- NOP
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

begin
    data <= ROM(to_int(addr));
end Behavioral;
