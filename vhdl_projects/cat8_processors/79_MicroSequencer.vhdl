-- ============================================================================
-- VHDL Project: MicroSequencer
-- Category: Processors
-- Complexity: High
-- Description: Microprogrammed sequencer with microcode ROM
--              Supports microcode branching, subroutines, and conditions
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity MicroSequencer is
    generic (
        UADDR_WIDTH : integer := 8;   -- Microcode address width
        UWORD_WIDTH : integer := 32;  -- Microcode word width
        OPCODE_WIDTH : integer := 6   -- Instruction opcode width
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Instruction opcode (for dispatch)
        opcode      : in  bit_vector(OPCODE_WIDTH-1 downto 0);
        -- Condition inputs
        zero        : in  bit;
        carry       : in  bit;
        negative    : in  bit;
        overflow    : in  bit;
        -- Microinstruction output
        micro_word  : out bit_vector(UWORD_WIDTH-1 downto 0);
        -- Control output fields
        ctrl_signals : out bit_vector(15 downto 0);
        alu_ctrl    : out bit_vector(3 downto 0);
        -- Status
        fetch_next  : out bit
    );
end MicroSequencer;

architecture Behavioral of MicroSequencer is
    -- Microinstruction format:
    -- [31:16] Control signals
    -- [15:12] ALU control
    -- [11:10] Next address select (00=inc, 01=branch, 10=dispatch, 11=return)
    -- [9:8]   Condition select (00=always, 01=zero, 10=carry, 11=neg)
    -- [7:0]   Branch/dispatch address

    constant SEL_INC      : bit_vector(1 downto 0) := "00";
    constant SEL_BRANCH   : bit_vector(1 downto 0) := "01";
    constant SEL_DISPATCH : bit_vector(1 downto 0) := "10";
    constant SEL_RETURN   : bit_vector(1 downto 0) := "11";

    constant COND_ALWAYS  : bit_vector(1 downto 0) := "00";
    constant COND_ZERO    : bit_vector(1 downto 0) := "01";
    constant COND_CARRY   : bit_vector(1 downto 0) := "10";
    constant COND_NEG     : bit_vector(1 downto 0) := "11";

    -- Microcode ROM
    type ucode_rom_type is array (0 to 255) of bit_vector(UWORD_WIDTH-1 downto 0);

    -- Example microcode (simplified)
    constant UCODE_ROM : ucode_rom_type := (
        -- Fetch cycle (addresses 0-3)
        0  => "00000001000000000000000000000001",  -- MAR <- PC
        1  => "00000010000000000000000000000010",  -- Read memory
        2  => "00000100000000000000000000000011",  -- IR <- MDR, PC++
        3  => "00000000000000001000000000000000",  -- Dispatch to opcode

        -- NOP (address 4)
        4  => "00000000000000000000000000000000",  -- Return to fetch

        -- ADD (addresses 8-11)
        8  => "00001000000000000000000000001001",  -- Read operand A
        9  => "00010000000000000000000000001010",  -- Read operand B
        10 => "00100000000100000000000000001011",  -- ALU ADD
        11 => "01000000000000001100000000000000",  -- Write result, return

        -- SUB (addresses 12-15)
        12 => "00001000000000000000000000001101",  -- Read operand A
        13 => "00010000000000000000000000001110",  -- Read operand B
        14 => "00100000001000000000000000001111",  -- ALU SUB
        15 => "01000000000000001100000000000000",  -- Write result, return

        -- LOAD (addresses 16-19)
        16 => "00000001000000000000000000010001",  -- MAR <- address
        17 => "00000010000000000000000000010010",  -- Read memory
        18 => "00000100000000000000000000010011",  -- Reg <- MDR
        19 => "00000000000000001100000000000000",  -- Return to fetch

        -- STORE (addresses 20-23)
        20 => "00000001000000000000000000010101",  -- MAR <- address
        21 => "00001000000000000000000000010110",  -- MDR <- Reg
        22 => "00000010000000000000000000010111",  -- Write memory
        23 => "00000000000000001100000000000000",  -- Return to fetch

        -- JUMP (addresses 24-25)
        24 => "10000000000000000000000000011001",  -- PC <- target
        25 => "00000000000000001100000000000000",  -- Return to fetch

        -- JUMP IF ZERO (addresses 26-28)
        26 => "00000000000000000100000000011100",  -- If zero, branch
        27 => "00000000000000001100000000000000",  -- Not zero, return
        28 => "10000000000000001100000000000000",  -- PC <- target, return

        others => "00000000000000001100000000000000"  -- Default: return to fetch
    );

    -- Dispatch ROM (maps opcode to microcode address)
    type dispatch_rom_type is array (0 to 63) of bit_vector(7 downto 0);
    constant DISPATCH_ROM : dispatch_rom_type := (
        0  => "00000100",  -- NOP -> address 4
        1  => "00001000",  -- ADD -> address 8
        2  => "00001100",  -- SUB -> address 12
        3  => "00010000",  -- LOAD -> address 16
        4  => "00010100",  -- STORE -> address 20
        5  => "00011000",  -- JUMP -> address 24
        6  => "00011010",  -- JZ -> address 26
        others => "00000100"  -- Default to NOP
    );

    signal upc : bit_vector(UADDR_WIDTH-1 downto 0);  -- Microcode PC
    signal uir : bit_vector(UWORD_WIDTH-1 downto 0);  -- Microinstruction register

    -- Return address stack for micro-subroutines
    type uras_type is array (0 to 3) of bit_vector(UADDR_WIDTH-1 downto 0);
    signal uras : uras_type;
    signal uras_ptr : integer range 0 to 4;

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
    process(clk, reset)
        variable next_upc : bit_vector(UADDR_WIDTH-1 downto 0);
        variable sel : bit_vector(1 downto 0);
        variable cond : bit_vector(1 downto 0);
        variable cond_met : bit;
        variable branch_addr : bit_vector(7 downto 0);
    begin
        if reset = '1' then
            upc <= (others => '0');
            uir <= (others => '0');
            uras_ptr <= 0;
            for i in 0 to 3 loop
                uras(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            -- Fetch microinstruction
            uir <= UCODE_ROM(to_int(upc));

            -- Decode next address selection
            sel := uir(11 downto 10);
            cond := uir(9 downto 8);
            branch_addr := uir(7 downto 0);

            -- Evaluate condition
            case cond is
                when COND_ALWAYS => cond_met := '1';
                when COND_ZERO   => cond_met := zero;
                when COND_CARRY  => cond_met := carry;
                when COND_NEG    => cond_met := negative;
                when others      => cond_met := '1';
            end case;

            -- Determine next microaddress
            case sel is
                when SEL_INC =>
                    next_upc := to_bv(to_int(upc) + 1, UADDR_WIDTH);

                when SEL_BRANCH =>
                    if cond_met = '1' then
                        next_upc := branch_addr;
                    else
                        next_upc := to_bv(to_int(upc) + 1, UADDR_WIDTH);
                    end if;

                when SEL_DISPATCH =>
                    -- Go to microcode routine for this opcode
                    next_upc := DISPATCH_ROM(to_int(opcode));
                    -- Push return address
                    uras(uras_ptr) <= to_bv(to_int(upc) + 1, UADDR_WIDTH);
                    if uras_ptr < 4 then
                        uras_ptr <= uras_ptr + 1;
                    end if;

                when SEL_RETURN =>
                    -- Return to fetch sequence
                    if uras_ptr > 0 then
                        next_upc := uras(uras_ptr - 1);
                        uras_ptr <= uras_ptr - 1;
                    else
                        next_upc := (others => '0');  -- Back to fetch
                    end if;

                when others =>
                    next_upc := to_bv(to_int(upc) + 1, UADDR_WIDTH);
            end case;

            upc <= next_upc;
        end if;
    end process;

    -- Output microinstruction fields
    micro_word <= uir;
    ctrl_signals <= uir(31 downto 16);
    alu_ctrl <= uir(15 downto 12);

    -- Signal when returning to fetch (for external use)
    fetch_next <= '1' when upc = "00000000" else '0';
end Behavioral;

-- ============================================================================
-- Microcode Assembler Support (Constant definitions)
-- ============================================================================
entity MicrocodeDefinitions is
end MicrocodeDefinitions;

architecture Definitions of MicrocodeDefinitions is
    -- Control signal bit positions
    constant CTRL_PC_INC    : integer := 0;
    constant CTRL_PC_LOAD   : integer := 1;
    constant CTRL_MAR_LOAD  : integer := 2;
    constant CTRL_MDR_LOAD  : integer := 3;
    constant CTRL_IR_LOAD   : integer := 4;
    constant CTRL_MEM_READ  : integer := 5;
    constant CTRL_MEM_WRITE : integer := 6;
    constant CTRL_REG_READ  : integer := 7;
    constant CTRL_REG_WRITE : integer := 8;
    constant CTRL_ALU_EN    : integer := 9;
    constant CTRL_A_SEL     : integer := 10;
    constant CTRL_B_SEL     : integer := 11;
    constant CTRL_FLAGS_EN  : integer := 12;

    -- ALU operations
    constant ALU_ADD  : bit_vector(3 downto 0) := "0000";
    constant ALU_SUB  : bit_vector(3 downto 0) := "0001";
    constant ALU_AND  : bit_vector(3 downto 0) := "0010";
    constant ALU_OR   : bit_vector(3 downto 0) := "0011";
    constant ALU_XOR  : bit_vector(3 downto 0) := "0100";
    constant ALU_NOT  : bit_vector(3 downto 0) := "0101";
    constant ALU_SHL  : bit_vector(3 downto 0) := "0110";
    constant ALU_SHR  : bit_vector(3 downto 0) := "0111";
    constant ALU_PASS : bit_vector(3 downto 0) := "1111";
begin
    -- This entity exists only to define constants
end Definitions;
