-- ============================================================================
-- VHDL Project: RISCVCore
-- Category: Processors
-- Complexity: Very High
-- Description: Simplified RISC-V RV32I core (single-cycle implementation)
--              Supports basic integer instructions
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity RISCVCore is
    generic (XLEN : integer := 32);
    port (
        clk           : in  bit;
        reset         : in  bit;
        -- Instruction memory interface
        imem_addr     : out bit_vector(XLEN-1 downto 0);
        imem_data     : in  bit_vector(XLEN-1 downto 0);
        -- Data memory interface
        dmem_addr     : out bit_vector(XLEN-1 downto 0);
        dmem_rdata    : in  bit_vector(XLEN-1 downto 0);
        dmem_wdata    : out bit_vector(XLEN-1 downto 0);
        dmem_we       : out bit;
        dmem_re       : out bit;
        dmem_size     : out bit_vector(1 downto 0);  -- 00=byte, 01=half, 10=word
        -- Debug
        debug_pc      : out bit_vector(XLEN-1 downto 0);
        halted        : out bit
    );
end RISCVCore;

architecture Behavioral of RISCVCore is
    -- RISC-V opcodes
    constant OP_LUI    : bit_vector(6 downto 0) := "0110111";
    constant OP_AUIPC  : bit_vector(6 downto 0) := "0010111";
    constant OP_JAL    : bit_vector(6 downto 0) := "1101111";
    constant OP_JALR   : bit_vector(6 downto 0) := "1100111";
    constant OP_BRANCH : bit_vector(6 downto 0) := "1100011";
    constant OP_LOAD   : bit_vector(6 downto 0) := "0000011";
    constant OP_STORE  : bit_vector(6 downto 0) := "0100011";
    constant OP_IMM    : bit_vector(6 downto 0) := "0010011";
    constant OP_REG    : bit_vector(6 downto 0) := "0110011";
    constant OP_FENCE  : bit_vector(6 downto 0) := "0001111";
    constant OP_SYSTEM : bit_vector(6 downto 0) := "1110011";

    -- Register file (32 registers)
    type reg_file_type is array (0 to 31) of bit_vector(XLEN-1 downto 0);
    signal regs : reg_file_type;

    -- Program counter
    signal pc : bit_vector(XLEN-1 downto 0);

    -- Instruction fields
    signal opcode : bit_vector(6 downto 0);
    signal rd, rs1, rs2 : bit_vector(4 downto 0);
    signal funct3 : bit_vector(2 downto 0);
    signal funct7 : bit_vector(6 downto 0);

    -- Internal signals
    signal inst : bit_vector(XLEN-1 downto 0);
    signal imm : bit_vector(XLEN-1 downto 0);
    signal rs1_data, rs2_data : bit_vector(XLEN-1 downto 0);
    signal alu_result : bit_vector(XLEN-1 downto 0);
    signal pc_next : bit_vector(XLEN-1 downto 0);
    signal reg_write_data : bit_vector(XLEN-1 downto 0);
    signal reg_write_en : bit;
    signal branch_taken : bit;

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

    function add_bv(a, b : bit_vector) return bit_vector is
        variable result : bit_vector(a'range);
        variable carry : bit := '0';
        variable sum : integer;
    begin
        for i in a'low to a'high loop
            sum := 0;
            if a(i) = '1' then sum := sum + 1; end if;
            if b(i) = '1' then sum := sum + 1; end if;
            if carry = '1' then sum := sum + 1; end if;
            if sum >= 2 then carry := '1'; else carry := '0'; end if;
            if (sum mod 2) = 1 then result(i) := '1'; else result(i) := '0'; end if;
        end loop;
        return result;
    end function;

begin
    -- Instruction fetch
    inst <= imem_data;
    imem_addr <= pc;

    -- Instruction decode
    opcode <= inst(6 downto 0);
    rd <= inst(11 downto 7);
    rs1 <= inst(19 downto 15);
    rs2 <= inst(24 downto 20);
    funct3 <= inst(14 downto 12);
    funct7 <= inst(31 downto 25);

    -- Register file read
    rs1_data <= (others => '0') when to_int(rs1) = 0 else regs(to_int(rs1));
    rs2_data <= (others => '0') when to_int(rs2) = 0 else regs(to_int(rs2));

    -- Immediate decode
    imm_decode: process(inst, opcode)
        variable imm_temp : bit_vector(XLEN-1 downto 0);
    begin
        imm_temp := (others => '0');

        case opcode is
            when OP_IMM | OP_LOAD | OP_JALR =>
                -- I-type
                imm_temp(11 downto 0) := inst(31 downto 20);
                for i in 12 to XLEN-1 loop
                    imm_temp(i) := inst(31);
                end loop;

            when OP_STORE =>
                -- S-type
                imm_temp(4 downto 0) := inst(11 downto 7);
                imm_temp(11 downto 5) := inst(31 downto 25);
                for i in 12 to XLEN-1 loop
                    imm_temp(i) := inst(31);
                end loop;

            when OP_BRANCH =>
                -- B-type
                imm_temp(0) := '0';
                imm_temp(4 downto 1) := inst(11 downto 8);
                imm_temp(10 downto 5) := inst(30 downto 25);
                imm_temp(11) := inst(7);
                imm_temp(12) := inst(31);
                for i in 13 to XLEN-1 loop
                    imm_temp(i) := inst(31);
                end loop;

            when OP_LUI | OP_AUIPC =>
                -- U-type
                imm_temp(31 downto 12) := inst(31 downto 12);

            when OP_JAL =>
                -- J-type
                imm_temp(0) := '0';
                imm_temp(10 downto 1) := inst(30 downto 21);
                imm_temp(11) := inst(20);
                imm_temp(19 downto 12) := inst(19 downto 12);
                imm_temp(20) := inst(31);
                for i in 21 to XLEN-1 loop
                    imm_temp(i) := inst(31);
                end loop;

            when others =>
                imm_temp := (others => '0');
        end case;

        imm <= imm_temp;
    end process;

    -- ALU
    alu: process(opcode, funct3, funct7, rs1_data, rs2_data, imm, pc)
        variable a, b : integer;
        variable result : integer;
        variable shift_amt : integer;
        variable alu_b : bit_vector(XLEN-1 downto 0);
    begin
        a := to_signed_int(rs1_data);

        -- Select ALU B input
        if opcode = OP_REG then
            alu_b := rs2_data;
        else
            alu_b := imm;
        end if;
        b := to_signed_int(alu_b);

        result := 0;

        case opcode is
            when OP_LUI =>
                result := to_int(imm);

            when OP_AUIPC =>
                result := to_int(pc) + to_int(imm);

            when OP_IMM | OP_REG =>
                case funct3 is
                    when "000" =>  -- ADD/ADDI or SUB
                        if opcode = OP_REG and funct7(5) = '1' then
                            result := a - b;  -- SUB
                        else
                            result := a + b;  -- ADD/ADDI
                        end if;

                    when "001" =>  -- SLL
                        shift_amt := to_int(alu_b(4 downto 0));
                        result := to_int(rs1_data) * (2 ** shift_amt);

                    when "010" =>  -- SLT
                        if a < b then result := 1; else result := 0; end if;

                    when "011" =>  -- SLTU
                        if to_int(rs1_data) < to_int(alu_b) then
                            result := 1;
                        else
                            result := 0;
                        end if;

                    when "100" =>  -- XOR
                        for i in 0 to XLEN-1 loop
                            if (rs1_data(i) xor alu_b(i)) = '1' then
                                result := result + (2 ** i);
                            end if;
                        end loop;

                    when "101" =>  -- SRL/SRA
                        shift_amt := to_int(alu_b(4 downto 0));
                        if funct7(5) = '1' then
                            -- SRA (arithmetic)
                            result := a / (2 ** shift_amt);
                        else
                            -- SRL (logical)
                            result := to_int(rs1_data) / (2 ** shift_amt);
                        end if;

                    when "110" =>  -- OR
                        for i in 0 to XLEN-1 loop
                            if (rs1_data(i) or alu_b(i)) = '1' then
                                result := result + (2 ** i);
                            end if;
                        end loop;

                    when "111" =>  -- AND
                        for i in 0 to XLEN-1 loop
                            if (rs1_data(i) and alu_b(i)) = '1' then
                                result := result + (2 ** i);
                            end if;
                        end loop;

                    when others =>
                        result := 0;
                end case;

            when OP_LOAD | OP_STORE =>
                result := a + b;  -- Address calculation

            when OP_JAL | OP_JALR =>
                result := to_int(pc) + 4;  -- Return address

            when others =>
                result := 0;
        end case;

        alu_result <= to_bv(result, XLEN);
    end process;

    -- Branch comparison
    branch_compare: process(opcode, funct3, rs1_data, rs2_data)
        variable a, b : integer;
        variable au, bu : integer;
    begin
        branch_taken <= '0';
        a := to_signed_int(rs1_data);
        b := to_signed_int(rs2_data);
        au := to_int(rs1_data);
        bu := to_int(rs2_data);

        if opcode = OP_BRANCH then
            case funct3 is
                when "000" => if a = b then branch_taken <= '1'; end if;  -- BEQ
                when "001" => if a /= b then branch_taken <= '1'; end if; -- BNE
                when "100" => if a < b then branch_taken <= '1'; end if;  -- BLT
                when "101" => if a >= b then branch_taken <= '1'; end if; -- BGE
                when "110" => if au < bu then branch_taken <= '1'; end if; -- BLTU
                when "111" => if au >= bu then branch_taken <= '1'; end if; -- BGEU
                when others => null;
            end case;
        end if;
    end process;

    -- Memory interface
    dmem_addr <= alu_result;
    dmem_wdata <= rs2_data;
    dmem_we <= '1' when opcode = OP_STORE else '0';
    dmem_re <= '1' when opcode = OP_LOAD else '0';
    dmem_size <= funct3(1 downto 0);

    -- Register write data select
    reg_write_data <= dmem_rdata when opcode = OP_LOAD else alu_result;

    -- Register write enable
    reg_write_en <= '1' when (opcode = OP_LUI or opcode = OP_AUIPC or
                              opcode = OP_JAL or opcode = OP_JALR or
                              opcode = OP_LOAD or opcode = OP_IMM or
                              opcode = OP_REG) and to_int(rd) /= 0 else '0';

    -- PC next calculation
    pc_next_calc: process(opcode, branch_taken, pc, imm, alu_result)
    begin
        if opcode = OP_JAL then
            pc_next <= add_bv(pc, imm);
        elsif opcode = OP_JALR then
            pc_next <= alu_result(XLEN-1 downto 1) & '0';
        elsif opcode = OP_BRANCH and branch_taken = '1' then
            pc_next <= add_bv(pc, imm);
        else
            pc_next <= add_bv(pc, to_bv(4, XLEN));
        end if;
    end process;

    -- Sequential logic
    process(clk, reset)
    begin
        if reset = '1' then
            pc <= (others => '0');
            halted <= '0';
            for i in 0 to 31 loop
                regs(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            -- ECALL/EBREAK halt
            if opcode = OP_SYSTEM then
                halted <= '1';
            else
                -- Update PC
                pc <= pc_next;

                -- Register write
                if reg_write_en = '1' then
                    regs(to_int(rd)) <= reg_write_data;
                end if;
            end if;
        end if;
    end process;

    debug_pc <= pc;
end Behavioral;
