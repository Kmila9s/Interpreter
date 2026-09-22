-- ============================================================================
-- VHDL Project: StackProcessor
-- Category: Processors
-- Complexity: High
-- Description: Stack-based processor (Forth-like architecture)
--              Dual stack design: data stack and return stack
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity StackProcessor is
    generic (
        DATA_WIDTH  : integer := 16;
        ADDR_WIDTH  : integer := 12;
        STACK_DEPTH : integer := 32
    );
    port (
        clk          : in  bit;
        reset        : in  bit;
        -- Program memory
        prog_addr    : out bit_vector(ADDR_WIDTH-1 downto 0);
        prog_data    : in  bit_vector(DATA_WIDTH-1 downto 0);
        -- Data memory
        mem_addr     : out bit_vector(ADDR_WIDTH-1 downto 0);
        mem_din      : in  bit_vector(DATA_WIDTH-1 downto 0);
        mem_dout     : out bit_vector(DATA_WIDTH-1 downto 0);
        mem_we       : out bit;
        -- I/O
        io_addr      : out bit_vector(7 downto 0);
        io_din       : in  bit_vector(DATA_WIDTH-1 downto 0);
        io_dout      : out bit_vector(DATA_WIDTH-1 downto 0);
        io_rd        : out bit;
        io_wr        : out bit;
        -- Status
        halted       : out bit
    );
end StackProcessor;

architecture Behavioral of StackProcessor is
    -- Instructions (5-bit opcode)
    constant OP_NOP   : bit_vector(4 downto 0) := "00000";
    constant OP_LIT   : bit_vector(4 downto 0) := "00001";  -- Push literal
    constant OP_DUP   : bit_vector(4 downto 0) := "00010";  -- Duplicate TOS
    constant OP_DROP  : bit_vector(4 downto 0) := "00011";  -- Drop TOS
    constant OP_SWAP  : bit_vector(4 downto 0) := "00100";  -- Swap TOS and NOS
    constant OP_OVER  : bit_vector(4 downto 0) := "00101";  -- Copy NOS to TOS
    constant OP_ROT   : bit_vector(4 downto 0) := "00110";  -- Rotate top 3
    constant OP_ADD   : bit_vector(4 downto 0) := "00111";  -- Add
    constant OP_SUB   : bit_vector(4 downto 0) := "01000";  -- Subtract
    constant OP_AND_I : bit_vector(4 downto 0) := "01001";  -- Bitwise AND
    constant OP_OR_I  : bit_vector(4 downto 0) := "01010";  -- Bitwise OR
    constant OP_XOR_I : bit_vector(4 downto 0) := "01011";  -- Bitwise XOR
    constant OP_NOT_I : bit_vector(4 downto 0) := "01100";  -- Bitwise NOT
    constant OP_EQ    : bit_vector(4 downto 0) := "01101";  -- Equal
    constant OP_LT    : bit_vector(4 downto 0) := "01110";  -- Less than
    constant OP_GT    : bit_vector(4 downto 0) := "01111";  -- Greater than
    constant OP_LOAD  : bit_vector(4 downto 0) := "10000";  -- Load from memory
    constant OP_STORE : bit_vector(4 downto 0) := "10001";  -- Store to memory
    constant OP_CALL  : bit_vector(4 downto 0) := "10010";  -- Call subroutine
    constant OP_RET   : bit_vector(4 downto 0) := "10011";  -- Return
    constant OP_JMP   : bit_vector(4 downto 0) := "10100";  -- Jump
    constant OP_JZ    : bit_vector(4 downto 0) := "10101";  -- Jump if zero
    constant OP_IN_I  : bit_vector(4 downto 0) := "10110";  -- I/O input
    constant OP_OUT_I : bit_vector(4 downto 0) := "10111";  -- I/O output
    constant OP_RPUSH : bit_vector(4 downto 0) := "11000";  -- Push to return stack
    constant OP_RPOP  : bit_vector(4 downto 0) := "11001";  -- Pop from return stack
    constant OP_MUL   : bit_vector(4 downto 0) := "11010";  -- Multiply
    constant OP_SHL   : bit_vector(4 downto 0) := "11011";  -- Shift left
    constant OP_SHR   : bit_vector(4 downto 0) := "11100";  -- Shift right
    constant OP_HALT  : bit_vector(4 downto 0) := "11111";  -- Halt

    type state_type is (FETCH, DECODE, EXECUTE, MEM_ACCESS, COMPLETE);
    signal state : state_type;

    -- Data stack
    type stack_array is array (0 to STACK_DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal dstack : stack_array;
    signal dsp : integer range 0 to STACK_DEPTH;  -- Data stack pointer

    -- Return stack
    signal rstack : stack_array;
    signal rsp : integer range 0 to STACK_DEPTH;  -- Return stack pointer

    -- Registers
    signal pc : bit_vector(ADDR_WIDTH-1 downto 0);
    signal ir : bit_vector(DATA_WIDTH-1 downto 0);

    -- TOS (Top Of Stack) register for faster access
    signal tos : bit_vector(DATA_WIDTH-1 downto 0);

    signal opcode : bit_vector(4 downto 0);
    signal operand : bit_vector(10 downto 0);

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

    function to_signed_int(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable weight : integer := 1;
    begin
        for i in 0 to bv'length-2 loop
            if bv(i) = '1' then result := result + weight; end if;
            weight := weight * 2;
        end loop;
        if bv(bv'length-1) = '1' then result := result - weight; end if;
        return result;
    end function;

    function to_bv(val : integer; width : integer) return bit_vector is
        variable result : bit_vector(width-1 downto 0);
        variable temp : integer := val;
    begin
        if temp < 0 then temp := temp + (2 ** width); end if;
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    opcode <= ir(15 downto 11);
    operand <= ir(10 downto 0);

    process(clk, reset)
        variable nos : bit_vector(DATA_WIDTH-1 downto 0);  -- Next On Stack
        variable result : integer;
        variable shift_amt : integer;
    begin
        if reset = '1' then
            state <= FETCH;
            pc <= (others => '0');
            ir <= (others => '0');
            tos <= (others => '0');
            dsp <= 0;
            rsp <= 0;
            halted <= '0';
            mem_we <= '0';
            io_rd <= '0';
            io_wr <= '0';
            for i in 0 to STACK_DEPTH-1 loop
                dstack(i) <= (others => '0');
                rstack(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            mem_we <= '0';
            io_rd <= '0';
            io_wr <= '0';

            case state is
                when FETCH =>
                    ir <= prog_data;
                    pc <= to_bv(to_int(pc) + 1, ADDR_WIDTH);
                    state <= DECODE;

                when DECODE =>
                    -- Get NOS if needed
                    if dsp > 0 then
                        nos := dstack(dsp - 1);
                    else
                        nos := (others => '0');
                    end if;

                    case opcode is
                        when OP_NOP =>
                            state <= FETCH;

                        when OP_LIT =>
                            -- Push literal (sign-extended operand)
                            if dsp > 0 then
                                dstack(dsp - 1) <= tos;
                            end if;
                            tos <= "00000" & operand;
                            dsp <= dsp + 1;
                            state <= FETCH;

                        when OP_DUP =>
                            if dsp > 0 then
                                dstack(dsp - 1) <= tos;
                            end if;
                            dsp <= dsp + 1;
                            state <= FETCH;

                        when OP_DROP =>
                            if dsp > 1 then
                                tos <= dstack(dsp - 2);
                                dsp <= dsp - 1;
                            elsif dsp > 0 then
                                tos <= (others => '0');
                                dsp <= 0;
                            end if;
                            state <= FETCH;

                        when OP_SWAP =>
                            if dsp > 1 then
                                dstack(dsp - 2) <= tos;
                                tos <= nos;
                            end if;
                            state <= FETCH;

                        when OP_OVER =>
                            if dsp > 0 then
                                dstack(dsp - 1) <= tos;
                            end if;
                            tos <= nos;
                            dsp <= dsp + 1;
                            state <= FETCH;

                        when OP_ADD =>
                            result := to_signed_int(tos) + to_signed_int(nos);
                            tos <= to_bv(result, DATA_WIDTH);
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_SUB =>
                            result := to_signed_int(nos) - to_signed_int(tos);
                            tos <= to_bv(result, DATA_WIDTH);
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_AND_I =>
                            for i in 0 to DATA_WIDTH-1 loop
                                tos(i) <= tos(i) and nos(i);
                            end loop;
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_OR_I =>
                            for i in 0 to DATA_WIDTH-1 loop
                                tos(i) <= tos(i) or nos(i);
                            end loop;
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_XOR_I =>
                            for i in 0 to DATA_WIDTH-1 loop
                                tos(i) <= tos(i) xor nos(i);
                            end loop;
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_NOT_I =>
                            for i in 0 to DATA_WIDTH-1 loop
                                tos(i) <= not tos(i);
                            end loop;
                            state <= FETCH;

                        when OP_EQ =>
                            if tos = nos then
                                tos <= (others => '1');
                            else
                                tos <= (others => '0');
                            end if;
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_LT =>
                            if to_signed_int(nos) < to_signed_int(tos) then
                                tos <= (others => '1');
                            else
                                tos <= (others => '0');
                            end if;
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_GT =>
                            if to_signed_int(nos) > to_signed_int(tos) then
                                tos <= (others => '1');
                            else
                                tos <= (others => '0');
                            end if;
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_LOAD =>
                            -- Address is in TOS
                            state <= MEM_ACCESS;

                        when OP_STORE =>
                            -- Address in TOS, data in NOS
                            mem_we <= '1';
                            state <= MEM_ACCESS;

                        when OP_CALL =>
                            -- Push return address to return stack
                            rstack(rsp) <= "0000" & pc;
                            rsp <= rsp + 1;
                            pc <= "0" & operand;
                            state <= FETCH;

                        when OP_RET =>
                            if rsp > 0 then
                                pc <= rstack(rsp - 1)(ADDR_WIDTH-1 downto 0);
                                rsp <= rsp - 1;
                            end if;
                            state <= FETCH;

                        when OP_JMP =>
                            pc <= "0" & operand;
                            state <= FETCH;

                        when OP_JZ =>
                            if tos = (tos'range => '0') then
                                pc <= "0" & operand;
                            end if;
                            -- Drop TOS
                            if dsp > 1 then
                                tos <= dstack(dsp - 2);
                            end if;
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_RPUSH =>
                            rstack(rsp) <= tos;
                            rsp <= rsp + 1;
                            if dsp > 1 then
                                tos <= dstack(dsp - 2);
                            end if;
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_RPOP =>
                            if dsp > 0 then
                                dstack(dsp - 1) <= tos;
                            end if;
                            if rsp > 0 then
                                tos <= rstack(rsp - 1);
                                rsp <= rsp - 1;
                            end if;
                            dsp <= dsp + 1;
                            state <= FETCH;

                        when OP_MUL =>
                            result := to_signed_int(tos) * to_signed_int(nos);
                            tos <= to_bv(result, DATA_WIDTH);
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_SHL =>
                            shift_amt := to_int(tos(3 downto 0));
                            result := to_int(nos) * (2 ** shift_amt);
                            tos <= to_bv(result mod (2 ** DATA_WIDTH), DATA_WIDTH);
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_SHR =>
                            shift_amt := to_int(tos(3 downto 0));
                            result := to_int(nos) / (2 ** shift_amt);
                            tos <= to_bv(result, DATA_WIDTH);
                            dsp <= dsp - 1;
                            state <= FETCH;

                        when OP_IN_I =>
                            io_rd <= '1';
                            state <= MEM_ACCESS;

                        when OP_OUT_I =>
                            io_wr <= '1';
                            state <= MEM_ACCESS;

                        when OP_HALT =>
                            halted <= '1';
                            state <= FETCH;

                        when others =>
                            state <= FETCH;
                    end case;

                when MEM_ACCESS =>
                    state <= COMPLETE;

                when COMPLETE =>
                    case opcode is
                        when OP_LOAD =>
                            tos <= mem_din;

                        when OP_STORE =>
                            -- Pop both TOS and NOS
                            if dsp > 2 then
                                tos <= dstack(dsp - 3);
                            end if;
                            dsp <= dsp - 2;

                        when OP_IN_I =>
                            tos <= io_din;

                        when OP_OUT_I =>
                            if dsp > 1 then
                                tos <= dstack(dsp - 2);
                            end if;
                            dsp <= dsp - 1;

                        when others =>
                            null;
                    end case;
                    state <= FETCH;
            end case;
        end if;
    end process;

    prog_addr <= pc;
    mem_addr <= tos(ADDR_WIDTH-1 downto 0);
    mem_dout <= dstack(dsp - 2) when dsp > 1 else (others => '0');
    io_addr <= tos(7 downto 0);
    io_dout <= dstack(dsp - 2) when dsp > 1 else (others => '0');
end Behavioral;
