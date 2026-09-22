-- ============================================================================
-- VHDL Project: PipelineRegisters
-- Category: Processors
-- Complexity: Medium-High
-- Description: Pipeline stage registers for a 5-stage RISC pipeline
--              IF/ID, ID/EX, EX/MEM, MEM/WB stages with stall/flush support
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

-- IF/ID Pipeline Register
entity IF_ID_Register is
    generic (
        INST_WIDTH : integer := 32;
        ADDR_WIDTH : integer := 32
    );
    port (
        clk       : in  bit;
        reset     : in  bit;
        stall     : in  bit;
        flush     : in  bit;
        -- Inputs from IF stage
        pc_in     : in  bit_vector(ADDR_WIDTH-1 downto 0);
        pc_plus4_in : in bit_vector(ADDR_WIDTH-1 downto 0);
        inst_in   : in  bit_vector(INST_WIDTH-1 downto 0);
        -- Outputs to ID stage
        pc_out    : out bit_vector(ADDR_WIDTH-1 downto 0);
        pc_plus4_out : out bit_vector(ADDR_WIDTH-1 downto 0);
        inst_out  : out bit_vector(INST_WIDTH-1 downto 0)
    );
end IF_ID_Register;

architecture Behavioral of IF_ID_Register is
begin
    process(clk, reset)
    begin
        if reset = '1' then
            pc_out <= (others => '0');
            pc_plus4_out <= (others => '0');
            inst_out <= (others => '0');  -- NOP
        elsif clk'event and clk = '1' then
            if flush = '1' then
                pc_out <= (others => '0');
                pc_plus4_out <= (others => '0');
                inst_out <= (others => '0');  -- Insert NOP (bubble)
            elsif stall = '0' then
                pc_out <= pc_in;
                pc_plus4_out <= pc_plus4_in;
                inst_out <= inst_in;
            end if;
            -- If stall='1', retain current values
        end if;
    end process;
end Behavioral;

-- ID/EX Pipeline Register
entity ID_EX_Register is
    generic (
        DATA_WIDTH : integer := 32;
        ADDR_WIDTH : integer := 32
    );
    port (
        clk          : in  bit;
        reset        : in  bit;
        stall        : in  bit;
        flush        : in  bit;
        -- Inputs from ID stage
        pc_in        : in  bit_vector(ADDR_WIDTH-1 downto 0);
        rs1_data_in  : in  bit_vector(DATA_WIDTH-1 downto 0);
        rs2_data_in  : in  bit_vector(DATA_WIDTH-1 downto 0);
        imm_in       : in  bit_vector(DATA_WIDTH-1 downto 0);
        rd_in        : in  bit_vector(4 downto 0);
        rs1_in       : in  bit_vector(4 downto 0);
        rs2_in       : in  bit_vector(4 downto 0);
        -- Control signals input
        reg_write_in : in  bit;
        mem_read_in  : in  bit;
        mem_write_in : in  bit;
        mem_to_reg_in : in bit;
        alu_src_in   : in  bit;
        alu_op_in    : in  bit_vector(3 downto 0);
        branch_in    : in  bit;
        jump_in      : in  bit;
        -- Outputs to EX stage
        pc_out       : out bit_vector(ADDR_WIDTH-1 downto 0);
        rs1_data_out : out bit_vector(DATA_WIDTH-1 downto 0);
        rs2_data_out : out bit_vector(DATA_WIDTH-1 downto 0);
        imm_out      : out bit_vector(DATA_WIDTH-1 downto 0);
        rd_out       : out bit_vector(4 downto 0);
        rs1_out      : out bit_vector(4 downto 0);
        rs2_out      : out bit_vector(4 downto 0);
        -- Control signals output
        reg_write_out : out bit;
        mem_read_out  : out bit;
        mem_write_out : out bit;
        mem_to_reg_out : out bit;
        alu_src_out   : out bit;
        alu_op_out    : out bit_vector(3 downto 0);
        branch_out    : out bit;
        jump_out      : out bit
    );
end ID_EX_Register;

architecture Behavioral of ID_EX_Register is
begin
    process(clk, reset)
    begin
        if reset = '1' then
            pc_out <= (others => '0');
            rs1_data_out <= (others => '0');
            rs2_data_out <= (others => '0');
            imm_out <= (others => '0');
            rd_out <= (others => '0');
            rs1_out <= (others => '0');
            rs2_out <= (others => '0');
            reg_write_out <= '0';
            mem_read_out <= '0';
            mem_write_out <= '0';
            mem_to_reg_out <= '0';
            alu_src_out <= '0';
            alu_op_out <= (others => '0');
            branch_out <= '0';
            jump_out <= '0';
        elsif clk'event and clk = '1' then
            if flush = '1' or stall = '1' then
                -- Insert bubble (NOP-like state)
                reg_write_out <= '0';
                mem_read_out <= '0';
                mem_write_out <= '0';
                mem_to_reg_out <= '0';
                branch_out <= '0';
                jump_out <= '0';
                rd_out <= (others => '0');
                alu_op_out <= (others => '0');
                if flush = '1' then
                    pc_out <= (others => '0');
                end if;
            else
                pc_out <= pc_in;
                rs1_data_out <= rs1_data_in;
                rs2_data_out <= rs2_data_in;
                imm_out <= imm_in;
                rd_out <= rd_in;
                rs1_out <= rs1_in;
                rs2_out <= rs2_in;
                reg_write_out <= reg_write_in;
                mem_read_out <= mem_read_in;
                mem_write_out <= mem_write_in;
                mem_to_reg_out <= mem_to_reg_in;
                alu_src_out <= alu_src_in;
                alu_op_out <= alu_op_in;
                branch_out <= branch_in;
                jump_out <= jump_in;
            end if;
        end if;
    end process;
end Behavioral;

-- EX/MEM Pipeline Register
entity EX_MEM_Register is
    generic (
        DATA_WIDTH : integer := 32;
        ADDR_WIDTH : integer := 32
    );
    port (
        clk           : in  bit;
        reset         : in  bit;
        flush         : in  bit;
        -- Inputs from EX stage
        pc_branch_in  : in  bit_vector(ADDR_WIDTH-1 downto 0);
        alu_result_in : in  bit_vector(DATA_WIDTH-1 downto 0);
        rs2_data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        rd_in         : in  bit_vector(4 downto 0);
        zero_in       : in  bit;
        -- Control signals input
        reg_write_in  : in  bit;
        mem_read_in   : in  bit;
        mem_write_in  : in  bit;
        mem_to_reg_in : in  bit;
        branch_in     : in  bit;
        -- Outputs to MEM stage
        pc_branch_out : out bit_vector(ADDR_WIDTH-1 downto 0);
        alu_result_out : out bit_vector(DATA_WIDTH-1 downto 0);
        rs2_data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        rd_out        : out bit_vector(4 downto 0);
        zero_out      : out bit;
        -- Control signals output
        reg_write_out : out bit;
        mem_read_out  : out bit;
        mem_write_out : out bit;
        mem_to_reg_out : out bit;
        branch_out    : out bit
    );
end EX_MEM_Register;

architecture Behavioral of EX_MEM_Register is
begin
    process(clk, reset)
    begin
        if reset = '1' then
            pc_branch_out <= (others => '0');
            alu_result_out <= (others => '0');
            rs2_data_out <= (others => '0');
            rd_out <= (others => '0');
            zero_out <= '0';
            reg_write_out <= '0';
            mem_read_out <= '0';
            mem_write_out <= '0';
            mem_to_reg_out <= '0';
            branch_out <= '0';
        elsif clk'event and clk = '1' then
            if flush = '1' then
                reg_write_out <= '0';
                mem_read_out <= '0';
                mem_write_out <= '0';
                mem_to_reg_out <= '0';
                branch_out <= '0';
                rd_out <= (others => '0');
            else
                pc_branch_out <= pc_branch_in;
                alu_result_out <= alu_result_in;
                rs2_data_out <= rs2_data_in;
                rd_out <= rd_in;
                zero_out <= zero_in;
                reg_write_out <= reg_write_in;
                mem_read_out <= mem_read_in;
                mem_write_out <= mem_write_in;
                mem_to_reg_out <= mem_to_reg_in;
                branch_out <= branch_in;
            end if;
        end if;
    end process;
end Behavioral;

-- MEM/WB Pipeline Register
entity MEM_WB_Register is
    generic (DATA_WIDTH : integer := 32);
    port (
        clk           : in  bit;
        reset         : in  bit;
        -- Inputs from MEM stage
        mem_data_in   : in  bit_vector(DATA_WIDTH-1 downto 0);
        alu_result_in : in  bit_vector(DATA_WIDTH-1 downto 0);
        rd_in         : in  bit_vector(4 downto 0);
        -- Control signals input
        reg_write_in  : in  bit;
        mem_to_reg_in : in  bit;
        -- Outputs to WB stage
        mem_data_out  : out bit_vector(DATA_WIDTH-1 downto 0);
        alu_result_out : out bit_vector(DATA_WIDTH-1 downto 0);
        rd_out        : out bit_vector(4 downto 0);
        -- Control signals output
        reg_write_out : out bit;
        mem_to_reg_out : out bit
    );
end MEM_WB_Register;

architecture Behavioral of MEM_WB_Register is
begin
    process(clk, reset)
    begin
        if reset = '1' then
            mem_data_out <= (others => '0');
            alu_result_out <= (others => '0');
            rd_out <= (others => '0');
            reg_write_out <= '0';
            mem_to_reg_out <= '0';
        elsif clk'event and clk = '1' then
            mem_data_out <= mem_data_in;
            alu_result_out <= alu_result_in;
            rd_out <= rd_in;
            reg_write_out <= reg_write_in;
            mem_to_reg_out <= mem_to_reg_in;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Hazard Detection Unit
-- ============================================================================
entity HazardDetectionUnit is
    port (
        -- From ID/EX register
        id_ex_mem_read : in  bit;
        id_ex_rd      : in  bit_vector(4 downto 0);
        -- From IF/ID register
        if_id_rs1     : in  bit_vector(4 downto 0);
        if_id_rs2     : in  bit_vector(4 downto 0);
        -- Hazard outputs
        stall         : out bit;
        pc_write      : out bit;
        if_id_write   : out bit
    );
end HazardDetectionUnit;

architecture Behavioral of HazardDetectionUnit is
begin
    process(id_ex_mem_read, id_ex_rd, if_id_rs1, if_id_rs2)
        variable hazard : bit;
    begin
        hazard := '0';

        -- Load-use hazard detection
        if id_ex_mem_read = '1' then
            if id_ex_rd = if_id_rs1 or id_ex_rd = if_id_rs2 then
                if id_ex_rd /= "00000" then  -- Not x0
                    hazard := '1';
                end if;
            end if;
        end if;

        stall <= hazard;
        pc_write <= not hazard;
        if_id_write <= not hazard;
    end process;
end Behavioral;

-- ============================================================================
-- Forwarding Unit
-- ============================================================================
entity ForwardingUnit is
    port (
        -- Source registers from ID/EX
        id_ex_rs1     : in  bit_vector(4 downto 0);
        id_ex_rs2     : in  bit_vector(4 downto 0);
        -- Destination registers and write enables
        ex_mem_rd     : in  bit_vector(4 downto 0);
        ex_mem_reg_wr : in  bit;
        mem_wb_rd     : in  bit_vector(4 downto 0);
        mem_wb_reg_wr : in  bit;
        -- Forward control signals
        forward_a     : out bit_vector(1 downto 0);
        forward_b     : out bit_vector(1 downto 0)
    );
end ForwardingUnit;

architecture Behavioral of ForwardingUnit is
begin
    -- Forward A (for rs1)
    process(id_ex_rs1, ex_mem_rd, ex_mem_reg_wr, mem_wb_rd, mem_wb_reg_wr)
    begin
        if ex_mem_reg_wr = '1' and ex_mem_rd /= "00000" and ex_mem_rd = id_ex_rs1 then
            forward_a <= "10";  -- Forward from EX/MEM
        elsif mem_wb_reg_wr = '1' and mem_wb_rd /= "00000" and mem_wb_rd = id_ex_rs1 then
            forward_a <= "01";  -- Forward from MEM/WB
        else
            forward_a <= "00";  -- No forwarding, use register file
        end if;
    end process;

    -- Forward B (for rs2)
    process(id_ex_rs2, ex_mem_rd, ex_mem_reg_wr, mem_wb_rd, mem_wb_reg_wr)
    begin
        if ex_mem_reg_wr = '1' and ex_mem_rd /= "00000" and ex_mem_rd = id_ex_rs2 then
            forward_b <= "10";
        elsif mem_wb_reg_wr = '1' and mem_wb_rd /= "00000" and mem_wb_rd = id_ex_rs2 then
            forward_b <= "01";
        else
            forward_b <= "00";
        end if;
    end process;
end Behavioral;
