-- ============================================================================
-- VHDL Project: InstructionDecoder
-- Category: Processors
-- Complexity: Medium-High
-- Description: RISC-style instruction decoder supporting multiple formats
--              R-type, I-type, J-type, B-type instruction decoding
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity InstructionDecoder is
    generic (INST_WIDTH : integer := 32);
    port (
        instruction   : in  bit_vector(INST_WIDTH-1 downto 0);
        -- Decoded fields
        opcode        : out bit_vector(6 downto 0);
        rd            : out bit_vector(4 downto 0);
        rs1           : out bit_vector(4 downto 0);
        rs2           : out bit_vector(4 downto 0);
        funct3        : out bit_vector(2 downto 0);
        funct7        : out bit_vector(6 downto 0);
        immediate     : out bit_vector(31 downto 0);
        -- Instruction type
        is_r_type     : out bit;
        is_i_type     : out bit;
        is_s_type     : out bit;
        is_b_type     : out bit;
        is_u_type     : out bit;
        is_j_type     : out bit;
        -- Control signals
        reg_write     : out bit;
        mem_read      : out bit;
        mem_write     : out bit;
        branch        : out bit;
        jump          : out bit;
        alu_src       : out bit;  -- '0' = reg, '1' = imm
        mem_to_reg    : out bit;
        -- ALU control
        alu_op        : out bit_vector(3 downto 0)
    );
end InstructionDecoder;

architecture Behavioral of InstructionDecoder is
    -- RISC-V opcode groups
    constant OP_LOAD   : bit_vector(6 downto 0) := "0000011";
    constant OP_STORE  : bit_vector(6 downto 0) := "0100011";
    constant OP_BRANCH : bit_vector(6 downto 0) := "1100011";
    constant OP_JAL    : bit_vector(6 downto 0) := "1101111";
    constant OP_JALR   : bit_vector(6 downto 0) := "1100111";
    constant OP_IMM    : bit_vector(6 downto 0) := "0010011";
    constant OP_REG    : bit_vector(6 downto 0) := "0110011";
    constant OP_LUI    : bit_vector(6 downto 0) := "0110111";
    constant OP_AUIPC  : bit_vector(6 downto 0) := "0010111";

    signal op_internal : bit_vector(6 downto 0);
    signal f3_internal : bit_vector(2 downto 0);
    signal f7_internal : bit_vector(6 downto 0);

begin
    -- Extract fixed fields
    op_internal <= instruction(6 downto 0);
    opcode <= op_internal;
    rd <= instruction(11 downto 7);
    rs1 <= instruction(19 downto 15);
    rs2 <= instruction(24 downto 20);
    f3_internal <= instruction(14 downto 12);
    funct3 <= f3_internal;
    f7_internal <= instruction(31 downto 25);
    funct7 <= f7_internal;

    -- Immediate extraction based on instruction type
    imm_decode: process(instruction, op_internal)
        variable imm_temp : bit_vector(31 downto 0);
    begin
        imm_temp := (others => '0');

        case op_internal is
            when OP_IMM | OP_LOAD | OP_JALR =>
                -- I-type: imm[11:0] = inst[31:20]
                imm_temp(11 downto 0) := instruction(31 downto 20);
                -- Sign extend
                for i in 12 to 31 loop
                    imm_temp(i) := instruction(31);
                end loop;

            when OP_STORE =>
                -- S-type: imm[11:5] = inst[31:25], imm[4:0] = inst[11:7]
                imm_temp(4 downto 0) := instruction(11 downto 7);
                imm_temp(11 downto 5) := instruction(31 downto 25);
                for i in 12 to 31 loop
                    imm_temp(i) := instruction(31);
                end loop;

            when OP_BRANCH =>
                -- B-type: imm[12|10:5|4:1|11]
                imm_temp(0) := '0';  -- Always 0 (halfword aligned)
                imm_temp(4 downto 1) := instruction(11 downto 8);
                imm_temp(10 downto 5) := instruction(30 downto 25);
                imm_temp(11) := instruction(7);
                imm_temp(12) := instruction(31);
                for i in 13 to 31 loop
                    imm_temp(i) := instruction(31);
                end loop;

            when OP_LUI | OP_AUIPC =>
                -- U-type: imm[31:12] = inst[31:12]
                imm_temp(31 downto 12) := instruction(31 downto 12);

            when OP_JAL =>
                -- J-type: imm[20|10:1|11|19:12]
                imm_temp(0) := '0';
                imm_temp(10 downto 1) := instruction(30 downto 21);
                imm_temp(11) := instruction(20);
                imm_temp(19 downto 12) := instruction(19 downto 12);
                imm_temp(20) := instruction(31);
                for i in 21 to 31 loop
                    imm_temp(i) := instruction(31);
                end loop;

            when others =>
                imm_temp := (others => '0');
        end case;

        immediate <= imm_temp;
    end process;

    -- Instruction type detection
    is_r_type <= '1' when op_internal = OP_REG else '0';
    is_i_type <= '1' when op_internal = OP_IMM or op_internal = OP_LOAD or
                          op_internal = OP_JALR else '0';
    is_s_type <= '1' when op_internal = OP_STORE else '0';
    is_b_type <= '1' when op_internal = OP_BRANCH else '0';
    is_u_type <= '1' when op_internal = OP_LUI or op_internal = OP_AUIPC else '0';
    is_j_type <= '1' when op_internal = OP_JAL else '0';

    -- Control signal generation
    control_gen: process(op_internal, f3_internal, f7_internal)
    begin
        -- Defaults
        reg_write <= '0';
        mem_read <= '0';
        mem_write <= '0';
        branch <= '0';
        jump <= '0';
        alu_src <= '0';
        mem_to_reg <= '0';
        alu_op <= "0000";

        case op_internal is
            when OP_REG =>
                reg_write <= '1';
                alu_src <= '0';  -- Register source
                -- ALU operation based on funct3 and funct7
                case f3_internal is
                    when "000" =>
                        if f7_internal(5) = '0' then
                            alu_op <= "0000";  -- ADD
                        else
                            alu_op <= "0001";  -- SUB
                        end if;
                    when "001" => alu_op <= "0010";  -- SLL
                    when "010" => alu_op <= "0011";  -- SLT
                    when "011" => alu_op <= "0100";  -- SLTU
                    when "100" => alu_op <= "0101";  -- XOR
                    when "101" =>
                        if f7_internal(5) = '0' then
                            alu_op <= "0110";  -- SRL
                        else
                            alu_op <= "0111";  -- SRA
                        end if;
                    when "110" => alu_op <= "1000";  -- OR
                    when "111" => alu_op <= "1001";  -- AND
                    when others => alu_op <= "0000";
                end case;

            when OP_IMM =>
                reg_write <= '1';
                alu_src <= '1';  -- Immediate source
                case f3_internal is
                    when "000" => alu_op <= "0000";  -- ADDI
                    when "001" => alu_op <= "0010";  -- SLLI
                    when "010" => alu_op <= "0011";  -- SLTI
                    when "011" => alu_op <= "0100";  -- SLTIU
                    when "100" => alu_op <= "0101";  -- XORI
                    when "101" =>
                        if f7_internal(5) = '0' then
                            alu_op <= "0110";  -- SRLI
                        else
                            alu_op <= "0111";  -- SRAI
                        end if;
                    when "110" => alu_op <= "1000";  -- ORI
                    when "111" => alu_op <= "1001";  -- ANDI
                    when others => alu_op <= "0000";
                end case;

            when OP_LOAD =>
                reg_write <= '1';
                mem_read <= '1';
                alu_src <= '1';
                mem_to_reg <= '1';
                alu_op <= "0000";  -- ADD for address calculation

            when OP_STORE =>
                mem_write <= '1';
                alu_src <= '1';
                alu_op <= "0000";

            when OP_BRANCH =>
                branch <= '1';
                alu_op <= "0001";  -- SUB for comparison

            when OP_JAL =>
                reg_write <= '1';
                jump <= '1';

            when OP_JALR =>
                reg_write <= '1';
                jump <= '1';
                alu_src <= '1';
                alu_op <= "0000";

            when OP_LUI =>
                reg_write <= '1';
                alu_op <= "1010";  -- Pass immediate

            when OP_AUIPC =>
                reg_write <= '1';
                alu_op <= "1011";  -- ADD PC + imm

            when others =>
                null;
        end case;
    end process;
end Behavioral;

-- ============================================================================
-- Simple 8-bit Instruction Decoder (for minimal CPU)
-- ============================================================================
entity SimpleDecoder is
    port (
        instruction : in  bit_vector(7 downto 0);
        -- Decoded signals
        opcode      : out bit_vector(3 downto 0);
        reg_dest    : out bit_vector(1 downto 0);
        reg_src     : out bit_vector(1 downto 0);
        -- Control signals
        alu_op      : out bit_vector(2 downto 0);
        reg_write   : out bit;
        mem_read    : out bit;
        mem_write   : out bit;
        branch      : out bit;
        use_imm     : out bit
    );
end SimpleDecoder;

architecture Behavioral of SimpleDecoder is
    signal op : bit_vector(3 downto 0);
begin
    op <= instruction(7 downto 4);
    opcode <= op;
    reg_dest <= instruction(3 downto 2);
    reg_src <= instruction(1 downto 0);

    process(op, instruction)
    begin
        -- Defaults
        alu_op <= "000";
        reg_write <= '0';
        mem_read <= '0';
        mem_write <= '0';
        branch <= '0';
        use_imm <= '0';

        case op is
            when "0000" =>  -- NOP
                null;

            when "0001" =>  -- MOV
                reg_write <= '1';
                alu_op <= "000";  -- Pass through

            when "0010" =>  -- ADD
                reg_write <= '1';
                alu_op <= "001";

            when "0011" =>  -- SUB
                reg_write <= '1';
                alu_op <= "010";

            when "0100" =>  -- AND
                reg_write <= '1';
                alu_op <= "011";

            when "0101" =>  -- OR
                reg_write <= '1';
                alu_op <= "100";

            when "0110" =>  -- XOR
                reg_write <= '1';
                alu_op <= "101";

            when "0111" =>  -- NOT
                reg_write <= '1';
                alu_op <= "110";

            when "1000" =>  -- LOAD
                reg_write <= '1';
                mem_read <= '1';

            when "1001" =>  -- STORE
                mem_write <= '1';

            when "1010" =>  -- LOADI (load immediate)
                reg_write <= '1';
                use_imm <= '1';

            when "1011" =>  -- CMP
                alu_op <= "010";  -- SUB without write

            when "1100" =>  -- JMP
                branch <= '1';

            when "1101" =>  -- JZ
                branch <= '1';

            when "1110" =>  -- JNZ
                branch <= '1';

            when others =>  -- HALT or undefined
                null;
        end case;
    end process;
end Behavioral;
