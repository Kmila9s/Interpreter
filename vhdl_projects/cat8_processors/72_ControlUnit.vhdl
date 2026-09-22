-- ============================================================================
-- VHDL Project: ControlUnit
-- Category: Processors
-- Complexity: High
-- Description: Microprogrammed control unit with microcode ROM
--              Generates control signals based on opcode and state
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ControlUnit is
    generic (
        OPCODE_WIDTH : integer := 6;
        CTRL_WIDTH   : integer := 24
    );
    port (
        clk          : in  bit;
        reset        : in  bit;
        -- Instruction input
        opcode       : in  bit_vector(OPCODE_WIDTH-1 downto 0);
        condition    : in  bit_vector(3 downto 0);  -- Condition codes from ALU
        -- Control outputs
        pc_inc       : out bit;
        pc_load      : out bit;
        ir_load      : out bit;
        mar_load     : out bit;
        mdr_load     : out bit;
        mem_read     : out bit;
        mem_write    : out bit;
        reg_read     : out bit;
        reg_write    : out bit;
        alu_op       : out bit_vector(3 downto 0);
        alu_src_a    : out bit_vector(1 downto 0);
        alu_src_b    : out bit_vector(1 downto 0);
        -- Status
        fetch_cycle  : out bit;
        exec_cycle   : out bit;
        halt         : out bit
    );
end ControlUnit;

architecture Behavioral of ControlUnit is
    -- Processor states
    type state_type is (FETCH1, FETCH2, FETCH3, DECODE,
                        EXEC1, EXEC2, EXEC3, EXEC4,
                        WRITEBACK, HALT_STATE);
    signal state : state_type;
    signal next_state : state_type;

    -- Instruction types
    constant OP_NOP   : bit_vector(5 downto 0) := "000000";
    constant OP_LOAD  : bit_vector(5 downto 0) := "000001";
    constant OP_STORE : bit_vector(5 downto 0) := "000010";
    constant OP_ADD   : bit_vector(5 downto 0) := "000011";
    constant OP_SUB   : bit_vector(5 downto 0) := "000100";
    constant OP_AND   : bit_vector(5 downto 0) := "000101";
    constant OP_OR    : bit_vector(5 downto 0) := "000110";
    constant OP_JMP   : bit_vector(5 downto 0) := "000111";
    constant OP_JZ    : bit_vector(5 downto 0) := "001000";
    constant OP_JNZ   : bit_vector(5 downto 0) := "001001";
    constant OP_CALL  : bit_vector(5 downto 0) := "001010";
    constant OP_RET   : bit_vector(5 downto 0) := "001011";
    constant OP_PUSH  : bit_vector(5 downto 0) := "001100";
    constant OP_POP   : bit_vector(5 downto 0) := "001101";
    constant OP_MOV   : bit_vector(5 downto 0) := "001110";
    constant OP_CMP   : bit_vector(5 downto 0) := "001111";
    constant OP_HALT  : bit_vector(5 downto 0) := "111111";

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
    -- State register
    state_reg: process(clk, reset)
    begin
        if reset = '1' then
            state <= FETCH1;
        elsif clk'event and clk = '1' then
            state <= next_state;
        end if;
    end process;

    -- Next state and output logic
    control_logic: process(state, opcode, condition)
        variable zero_flag : bit;
        variable neg_flag : bit;
    begin
        -- Default outputs
        pc_inc <= '0';
        pc_load <= '0';
        ir_load <= '0';
        mar_load <= '0';
        mdr_load <= '0';
        mem_read <= '0';
        mem_write <= '0';
        reg_read <= '0';
        reg_write <= '0';
        alu_op <= "0000";
        alu_src_a <= "00";
        alu_src_b <= "00";
        fetch_cycle <= '0';
        exec_cycle <= '0';
        halt <= '0';

        next_state <= state;
        zero_flag := condition(3);
        neg_flag := condition(0);

        case state is
            -- Instruction Fetch
            when FETCH1 =>
                fetch_cycle <= '1';
                mar_load <= '1';      -- MAR <- PC
                next_state <= FETCH2;

            when FETCH2 =>
                fetch_cycle <= '1';
                mem_read <= '1';      -- Read instruction from memory
                next_state <= FETCH3;

            when FETCH3 =>
                fetch_cycle <= '1';
                ir_load <= '1';       -- IR <- MDR
                pc_inc <= '1';        -- PC++
                next_state <= DECODE;

            -- Instruction Decode
            when DECODE =>
                exec_cycle <= '1';

                case opcode is
                    when OP_NOP =>
                        next_state <= FETCH1;

                    when OP_HALT =>
                        next_state <= HALT_STATE;

                    when OP_LOAD =>
                        next_state <= EXEC1;

                    when OP_STORE =>
                        next_state <= EXEC1;

                    when OP_ADD | OP_SUB | OP_AND | OP_OR | OP_CMP =>
                        reg_read <= '1';
                        next_state <= EXEC1;

                    when OP_JMP =>
                        next_state <= EXEC1;

                    when OP_JZ =>
                        if zero_flag = '1' then
                            next_state <= EXEC1;
                        else
                            next_state <= FETCH1;
                        end if;

                    when OP_JNZ =>
                        if zero_flag = '0' then
                            next_state <= EXEC1;
                        else
                            next_state <= FETCH1;
                        end if;

                    when OP_MOV =>
                        reg_read <= '1';
                        next_state <= WRITEBACK;

                    when OP_CALL | OP_RET | OP_PUSH | OP_POP =>
                        next_state <= EXEC1;

                    when others =>
                        next_state <= FETCH1;
                end case;

            -- Execute cycles
            when EXEC1 =>
                exec_cycle <= '1';

                case opcode is
                    when OP_LOAD =>
                        mar_load <= '1';  -- MAR <- address from IR
                        next_state <= EXEC2;

                    when OP_STORE =>
                        mar_load <= '1';
                        reg_read <= '1';
                        next_state <= EXEC2;

                    when OP_ADD =>
                        alu_op <= "0000";  -- ADD
                        alu_src_a <= "00"; -- Register A
                        alu_src_b <= "00"; -- Register B
                        next_state <= WRITEBACK;

                    when OP_SUB | OP_CMP =>
                        alu_op <= "0010";  -- SUB
                        next_state <= WRITEBACK;

                    when OP_AND =>
                        alu_op <= "0100";  -- AND
                        next_state <= WRITEBACK;

                    when OP_OR =>
                        alu_op <= "0101";  -- OR
                        next_state <= WRITEBACK;

                    when OP_JMP | OP_JZ | OP_JNZ =>
                        pc_load <= '1';   -- PC <- target address
                        next_state <= FETCH1;

                    when OP_CALL =>
                        -- Push return address
                        next_state <= EXEC2;

                    when OP_RET =>
                        -- Pop return address
                        next_state <= EXEC2;

                    when OP_PUSH =>
                        -- Decrement SP, write to stack
                        next_state <= EXEC2;

                    when OP_POP =>
                        -- Read from stack, increment SP
                        next_state <= EXEC2;

                    when others =>
                        next_state <= FETCH1;
                end case;

            when EXEC2 =>
                exec_cycle <= '1';

                case opcode is
                    when OP_LOAD =>
                        mem_read <= '1';
                        next_state <= EXEC3;

                    when OP_STORE =>
                        mem_write <= '1';
                        next_state <= FETCH1;

                    when OP_CALL =>
                        mem_write <= '1';  -- Write return address
                        next_state <= EXEC3;

                    when OP_RET =>
                        mem_read <= '1';
                        next_state <= EXEC3;

                    when OP_PUSH =>
                        mem_write <= '1';
                        next_state <= FETCH1;

                    when OP_POP =>
                        mem_read <= '1';
                        next_state <= EXEC3;

                    when others =>
                        next_state <= FETCH1;
                end case;

            when EXEC3 =>
                exec_cycle <= '1';

                case opcode is
                    when OP_LOAD =>
                        mdr_load <= '1';
                        next_state <= WRITEBACK;

                    when OP_CALL =>
                        pc_load <= '1';   -- Jump to subroutine
                        next_state <= FETCH1;

                    when OP_RET =>
                        pc_load <= '1';   -- Return from subroutine
                        next_state <= FETCH1;

                    when OP_POP =>
                        next_state <= WRITEBACK;

                    when others =>
                        next_state <= FETCH1;
                end case;

            when EXEC4 =>
                exec_cycle <= '1';
                next_state <= FETCH1;

            -- Writeback
            when WRITEBACK =>
                if opcode /= OP_CMP then  -- CMP doesn't write back
                    reg_write <= '1';
                end if;
                next_state <= FETCH1;

            -- Halt state
            when HALT_STATE =>
                halt <= '1';
                -- Stay in halt until reset
        end case;
    end process;
end Behavioral;

-- ============================================================================
-- Hardwired Control Unit (combinational)
-- ============================================================================
entity HardwiredControl is
    port (
        opcode    : in  bit_vector(5 downto 0);
        phase     : in  bit_vector(2 downto 0);
        flags     : in  bit_vector(3 downto 0);
        -- Control signals
        ctrl_bus  : out bit_vector(15 downto 0)
    );
end HardwiredControl;

architecture Behavioral of HardwiredControl is
    -- Control signal bit positions
    constant PC_INC    : integer := 0;
    constant PC_LOAD   : integer := 1;
    constant IR_LOAD   : integer := 2;
    constant MAR_LOAD  : integer := 3;
    constant MDR_LOAD  : integer := 4;
    constant MEM_RD    : integer := 5;
    constant MEM_WR    : integer := 6;
    constant REG_RD    : integer := 7;
    constant REG_WR    : integer := 8;
    constant ALU_EN    : integer := 9;

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
    process(opcode, phase, flags)
        variable ctrl : bit_vector(15 downto 0);
        variable phase_int : integer;
    begin
        ctrl := (others => '0');
        phase_int := to_int(phase);

        -- Common fetch phases
        if phase_int = 0 then
            ctrl(MAR_LOAD) := '1';
        elsif phase_int = 1 then
            ctrl(MEM_RD) := '1';
        elsif phase_int = 2 then
            ctrl(IR_LOAD) := '1';
            ctrl(PC_INC) := '1';
        else
            -- Execute phases depend on opcode
            case opcode is
                when "000001" =>  -- LOAD
                    if phase_int = 3 then ctrl(MAR_LOAD) := '1';
                    elsif phase_int = 4 then ctrl(MEM_RD) := '1';
                    elsif phase_int = 5 then ctrl(REG_WR) := '1';
                    end if;

                when "000010" =>  -- STORE
                    if phase_int = 3 then ctrl(MAR_LOAD) := '1'; ctrl(REG_RD) := '1';
                    elsif phase_int = 4 then ctrl(MEM_WR) := '1';
                    end if;

                when "000011" | "000100" | "000101" | "000110" =>  -- ALU ops
                    if phase_int = 3 then ctrl(REG_RD) := '1'; ctrl(ALU_EN) := '1';
                    elsif phase_int = 4 then ctrl(REG_WR) := '1';
                    end if;

                when "000111" =>  -- JMP
                    if phase_int = 3 then ctrl(PC_LOAD) := '1';
                    end if;

                when "001000" =>  -- JZ
                    if phase_int = 3 and flags(3) = '1' then
                        ctrl(PC_LOAD) := '1';
                    end if;

                when others =>
                    null;
            end case;
        end if;

        ctrl_bus <= ctrl;
    end process;
end Behavioral;
