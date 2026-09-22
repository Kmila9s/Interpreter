-- ============================================================================
-- VHDL Project: ALU (Arithmetic Logic Unit)
-- Category: Processors
-- Complexity: Medium-High
-- Description: Full-featured ALU with arithmetic, logic, and shift operations
--              Flags: Zero, Carry, Overflow, Negative
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ALU is
    generic (DATA_WIDTH : integer := 16);
    port (
        operand_a  : in  bit_vector(DATA_WIDTH-1 downto 0);
        operand_b  : in  bit_vector(DATA_WIDTH-1 downto 0);
        operation  : in  bit_vector(3 downto 0);
        carry_in   : in  bit;
        result     : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Flags
        zero       : out bit;
        carry      : out bit;
        overflow   : out bit;
        negative   : out bit
    );
end ALU;

architecture Behavioral of ALU is
    -- ALU Operations
    constant OP_ADD   : bit_vector(3 downto 0) := "0000";
    constant OP_ADC   : bit_vector(3 downto 0) := "0001";
    constant OP_SUB   : bit_vector(3 downto 0) := "0010";
    constant OP_SBC   : bit_vector(3 downto 0) := "0011";
    constant OP_AND   : bit_vector(3 downto 0) := "0100";
    constant OP_OR    : bit_vector(3 downto 0) := "0101";
    constant OP_XOR   : bit_vector(3 downto 0) := "0110";
    constant OP_NOT   : bit_vector(3 downto 0) := "0111";
    constant OP_SHL   : bit_vector(3 downto 0) := "1000";
    constant OP_SHR   : bit_vector(3 downto 0) := "1001";
    constant OP_SAR   : bit_vector(3 downto 0) := "1010";  -- Arithmetic shift right
    constant OP_ROL   : bit_vector(3 downto 0) := "1011";
    constant OP_ROR   : bit_vector(3 downto 0) := "1100";
    constant OP_CMP   : bit_vector(3 downto 0) := "1101";  -- Compare (sub without result)
    constant OP_NEG   : bit_vector(3 downto 0) := "1110";  -- Two's complement
    constant OP_PASS  : bit_vector(3 downto 0) := "1111";  -- Pass A through

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
    process(operand_a, operand_b, operation, carry_in)
        variable a, b : integer;
        variable sum : integer;
        variable res : bit_vector(DATA_WIDTH-1 downto 0);
        variable c_in : integer;
        variable c_out : bit;
        variable shift_amt : integer;
        variable ov : bit;
    begin
        a := to_int(operand_a);
        b := to_int(operand_b);
        if carry_in = '1' then c_in := 1; else c_in := 0; end if;

        c_out := '0';
        ov := '0';

        case operation is
            when OP_ADD =>
                sum := a + b;
                if sum >= 2**DATA_WIDTH then
                    c_out := '1';
                    sum := sum - 2**DATA_WIDTH;
                end if;
                -- Overflow: signs of operands same, result different
                if operand_a(DATA_WIDTH-1) = operand_b(DATA_WIDTH-1) then
                    if to_bv(sum, DATA_WIDTH)(DATA_WIDTH-1) /= operand_a(DATA_WIDTH-1) then
                        ov := '1';
                    end if;
                end if;
                res := to_bv(sum, DATA_WIDTH);

            when OP_ADC =>
                sum := a + b + c_in;
                if sum >= 2**DATA_WIDTH then
                    c_out := '1';
                    sum := sum - 2**DATA_WIDTH;
                end if;
                if operand_a(DATA_WIDTH-1) = operand_b(DATA_WIDTH-1) then
                    if to_bv(sum, DATA_WIDTH)(DATA_WIDTH-1) /= operand_a(DATA_WIDTH-1) then
                        ov := '1';
                    end if;
                end if;
                res := to_bv(sum, DATA_WIDTH);

            when OP_SUB | OP_CMP =>
                sum := a - b;
                if sum < 0 then
                    c_out := '1';  -- Borrow
                    sum := sum + 2**DATA_WIDTH;
                end if;
                -- Overflow: signs of operands different, result same as subtrahend
                if operand_a(DATA_WIDTH-1) /= operand_b(DATA_WIDTH-1) then
                    if to_bv(sum, DATA_WIDTH)(DATA_WIDTH-1) = operand_b(DATA_WIDTH-1) then
                        ov := '1';
                    end if;
                end if;
                res := to_bv(sum, DATA_WIDTH);

            when OP_SBC =>
                sum := a - b - c_in;
                if sum < 0 then
                    c_out := '1';
                    sum := sum + 2**DATA_WIDTH;
                end if;
                res := to_bv(sum, DATA_WIDTH);

            when OP_AND =>
                for i in 0 to DATA_WIDTH-1 loop
                    res(i) := operand_a(i) and operand_b(i);
                end loop;

            when OP_OR =>
                for i in 0 to DATA_WIDTH-1 loop
                    res(i) := operand_a(i) or operand_b(i);
                end loop;

            when OP_XOR =>
                for i in 0 to DATA_WIDTH-1 loop
                    res(i) := operand_a(i) xor operand_b(i);
                end loop;

            when OP_NOT =>
                for i in 0 to DATA_WIDTH-1 loop
                    res(i) := not operand_a(i);
                end loop;

            when OP_SHL =>
                shift_amt := to_int(operand_b(3 downto 0));
                if shift_amt > 0 then
                    c_out := operand_a(DATA_WIDTH - shift_amt);
                end if;
                res := to_bv((a * (2 ** shift_amt)) mod (2 ** DATA_WIDTH), DATA_WIDTH);

            when OP_SHR =>
                shift_amt := to_int(operand_b(3 downto 0));
                if shift_amt > 0 then
                    c_out := operand_a(shift_amt - 1);
                end if;
                res := to_bv(a / (2 ** shift_amt), DATA_WIDTH);

            when OP_SAR =>
                shift_amt := to_int(operand_b(3 downto 0));
                if shift_amt > 0 then
                    c_out := operand_a(shift_amt - 1);
                end if;
                sum := a / (2 ** shift_amt);
                -- Sign extend
                if operand_a(DATA_WIDTH-1) = '1' then
                    for i in 0 to shift_amt-1 loop
                        sum := sum + 2 ** (DATA_WIDTH - 1 - i);
                    end loop;
                end if;
                res := to_bv(sum, DATA_WIDTH);

            when OP_ROL =>
                shift_amt := to_int(operand_b(3 downto 0)) mod DATA_WIDTH;
                c_out := operand_a(DATA_WIDTH - 1 - shift_amt + 1);
                sum := ((a * (2 ** shift_amt)) mod (2 ** DATA_WIDTH)) +
                       (a / (2 ** (DATA_WIDTH - shift_amt)));
                res := to_bv(sum mod (2 ** DATA_WIDTH), DATA_WIDTH);

            when OP_ROR =>
                shift_amt := to_int(operand_b(3 downto 0)) mod DATA_WIDTH;
                if shift_amt > 0 then
                    c_out := operand_a(shift_amt - 1);
                end if;
                sum := (a / (2 ** shift_amt)) +
                       ((a mod (2 ** shift_amt)) * (2 ** (DATA_WIDTH - shift_amt)));
                res := to_bv(sum, DATA_WIDTH);

            when OP_NEG =>
                sum := (2 ** DATA_WIDTH) - a;
                res := to_bv(sum mod (2 ** DATA_WIDTH), DATA_WIDTH);
                if a /= 0 then c_out := '1'; end if;

            when others =>  -- OP_PASS
                res := operand_a;
        end case;

        result <= res;

        -- Set flags
        if res = (res'range => '0') then
            zero <= '1';
        else
            zero <= '0';
        end if;

        carry <= c_out;
        overflow <= ov;
        negative <= res(DATA_WIDTH-1);
    end process;
end Behavioral;

-- ============================================================================
-- Extended ALU with multiply/divide
-- ============================================================================
entity ALUExtended is
    generic (DATA_WIDTH : integer := 16);
    port (
        clk       : in  bit;
        start     : in  bit;
        operand_a : in  bit_vector(DATA_WIDTH-1 downto 0);
        operand_b : in  bit_vector(DATA_WIDTH-1 downto 0);
        operation : in  bit_vector(4 downto 0);
        result_lo : out bit_vector(DATA_WIDTH-1 downto 0);
        result_hi : out bit_vector(DATA_WIDTH-1 downto 0);
        busy      : out bit;
        done      : out bit;
        flags     : out bit_vector(3 downto 0)  -- Z, C, V, N
    );
end ALUExtended;

architecture Behavioral of ALUExtended is
    constant OP_MUL   : bit_vector(4 downto 0) := "10000";
    constant OP_MULS  : bit_vector(4 downto 0) := "10001";  -- Signed multiply
    constant OP_DIV   : bit_vector(4 downto 0) := "10010";
    constant OP_DIVS  : bit_vector(4 downto 0) := "10011";  -- Signed divide
    constant OP_MOD   : bit_vector(4 downto 0) := "10100";

    type state_type is (IDLE, MULTIPLY, DIVIDE, COMPLETE);
    signal state : state_type;

    signal acc : bit_vector(DATA_WIDTH*2-1 downto 0);
    signal divisor : bit_vector(DATA_WIDTH-1 downto 0);
    signal count : integer range 0 to DATA_WIDTH;
    signal sign_a, sign_b, sign_result : bit;

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
        if temp < 0 then temp := temp + (2 ** width); end if;
        for i in 0 to width-1 loop
            if (temp mod 2) = 1 then result(i) := '1';
            else result(i) := '0'; end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    process(clk)
        variable a_val, b_val : integer;
        variable product : integer;
        variable quotient, remainder : integer;
    begin
        if clk'event and clk = '1' then
            done <= '0';

            case state is
                when IDLE =>
                    busy <= '0';
                    if start = '1' then
                        busy <= '1';

                        case operation is
                            when OP_MUL =>
                                a_val := to_int(operand_a);
                                b_val := to_int(operand_b);
                                product := a_val * b_val;
                                result_lo <= to_bv(product mod (2 ** DATA_WIDTH), DATA_WIDTH);
                                result_hi <= to_bv(product / (2 ** DATA_WIDTH), DATA_WIDTH);
                                done <= '1';

                            when OP_MULS =>
                                -- Signed multiply
                                sign_a <= operand_a(DATA_WIDTH-1);
                                sign_b <= operand_b(DATA_WIDTH-1);
                                a_val := to_int(operand_a);
                                b_val := to_int(operand_b);
                                -- Convert to signed
                                if operand_a(DATA_WIDTH-1) = '1' then
                                    a_val := a_val - (2 ** DATA_WIDTH);
                                end if;
                                if operand_b(DATA_WIDTH-1) = '1' then
                                    b_val := b_val - (2 ** DATA_WIDTH);
                                end if;
                                product := a_val * b_val;
                                if product < 0 then
                                    product := product + (2 ** (DATA_WIDTH * 2));
                                end if;
                                result_lo <= to_bv(product mod (2 ** DATA_WIDTH), DATA_WIDTH);
                                result_hi <= to_bv(product / (2 ** DATA_WIDTH), DATA_WIDTH);
                                done <= '1';

                            when OP_DIV | OP_MOD =>
                                a_val := to_int(operand_a);
                                b_val := to_int(operand_b);
                                if b_val /= 0 then
                                    quotient := a_val / b_val;
                                    remainder := a_val mod b_val;
                                else
                                    quotient := (2 ** DATA_WIDTH) - 1;  -- Max value for div by zero
                                    remainder := a_val;
                                end if;
                                result_lo <= to_bv(quotient, DATA_WIDTH);
                                result_hi <= to_bv(remainder, DATA_WIDTH);
                                done <= '1';

                            when others =>
                                -- Simple ALU operations handled combinatorially
                                done <= '1';
                        end case;

                        state <= COMPLETE;
                    end if;

                when COMPLETE =>
                    done <= '1';
                    state <= IDLE;

                when others =>
                    state <= IDLE;
            end case;

            -- Set flags based on result_lo
            if result_lo = (result_lo'range => '0') then
                flags(3) <= '1';  -- Zero
            else
                flags(3) <= '0';
            end if;
            flags(0) <= result_lo(DATA_WIDTH-1);  -- Negative
        end if;
    end process;
end Behavioral;
