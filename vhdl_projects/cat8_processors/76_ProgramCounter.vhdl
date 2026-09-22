-- ============================================================================
-- VHDL Project: ProgramCounter
-- Category: Processors
-- Complexity: Medium
-- Description: Program counter with branch, jump, and interrupt support
--              Includes branch predictor and return address stack
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity ProgramCounter is
    generic (ADDR_WIDTH : integer := 32);
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Control inputs
        enable      : in  bit;
        load        : in  bit;
        -- Address inputs
        load_addr   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        increment   : in  bit_vector(ADDR_WIDTH-1 downto 0);  -- Usually 4
        -- Outputs
        pc_out      : out bit_vector(ADDR_WIDTH-1 downto 0);
        pc_plus4    : out bit_vector(ADDR_WIDTH-1 downto 0)
    );
end ProgramCounter;

architecture Behavioral of ProgramCounter is
    signal pc_reg : bit_vector(ADDR_WIDTH-1 downto 0);

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
    process(clk, reset)
    begin
        if reset = '1' then
            pc_reg <= (others => '0');
        elsif clk'event and clk = '1' then
            if load = '1' then
                pc_reg <= load_addr;
            elsif enable = '1' then
                pc_reg <= add_bv(pc_reg, increment);
            end if;
        end if;
    end process;

    pc_out <= pc_reg;
    pc_plus4 <= add_bv(pc_reg, increment);
end Behavioral;

-- ============================================================================
-- Program Counter with Branch Target Buffer (BTB)
-- ============================================================================
entity PCWithBTB is
    generic (
        ADDR_WIDTH : integer := 32;
        BTB_SIZE   : integer := 16;  -- Number of BTB entries
        BTB_BITS   : integer := 4    -- log2(BTB_SIZE)
    );
    port (
        clk           : in  bit;
        reset         : in  bit;
        stall         : in  bit;
        -- Branch resolution
        branch_taken  : in  bit;
        branch_target : in  bit_vector(ADDR_WIDTH-1 downto 0);
        branch_pc     : in  bit_vector(ADDR_WIDTH-1 downto 0);
        update_btb    : in  bit;
        -- Jump/exception
        jump          : in  bit;
        jump_target   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        exception     : in  bit;
        exc_handler   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        -- Outputs
        pc_out        : out bit_vector(ADDR_WIDTH-1 downto 0);
        predicted_pc  : out bit_vector(ADDR_WIDTH-1 downto 0);
        prediction    : out bit
    );
end PCWithBTB;

architecture Behavioral of PCWithBTB is
    type btb_entry is record
        valid  : bit;
        tag    : bit_vector(ADDR_WIDTH-BTB_BITS-3 downto 0);
        target : bit_vector(ADDR_WIDTH-1 downto 0);
        taken  : bit_vector(1 downto 0);  -- 2-bit saturating counter
    end record;

    type btb_array is array (0 to BTB_SIZE-1) of btb_entry;
    signal btb : btb_array;

    signal pc_reg : bit_vector(ADDR_WIDTH-1 downto 0);

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
    process(clk, reset)
        variable btb_idx : integer;
        variable tag : bit_vector(ADDR_WIDTH-BTB_BITS-3 downto 0);
        variable counter : integer;
    begin
        if reset = '1' then
            pc_reg <= (others => '0');
            for i in 0 to BTB_SIZE-1 loop
                btb(i).valid <= '0';
                btb(i).taken <= "01";  -- Weakly not taken
            end loop;
        elsif clk'event and clk = '1' then
            -- Update BTB on branch resolution
            if update_btb = '1' then
                btb_idx := to_int(branch_pc(BTB_BITS+1 downto 2));
                btb(btb_idx).valid <= '1';
                btb(btb_idx).tag <= branch_pc(ADDR_WIDTH-1 downto BTB_BITS+2);
                btb(btb_idx).target <= branch_target;

                -- Update 2-bit counter
                counter := to_int(btb(btb_idx).taken);
                if branch_taken = '1' then
                    if counter < 3 then counter := counter + 1; end if;
                else
                    if counter > 0 then counter := counter - 1; end if;
                end if;
                btb(btb_idx).taken <= to_bv(counter, 2);
            end if;

            -- PC update logic
            if exception = '1' then
                pc_reg <= exc_handler;
            elsif jump = '1' then
                pc_reg <= jump_target;
            elsif stall = '0' then
                -- Check BTB for prediction
                btb_idx := to_int(pc_reg(BTB_BITS+1 downto 2));
                tag := pc_reg(ADDR_WIDTH-1 downto BTB_BITS+2);

                if btb(btb_idx).valid = '1' and
                   btb(btb_idx).tag = tag and
                   btb(btb_idx).taken(1) = '1' then
                    -- Predict taken
                    pc_reg <= btb(btb_idx).target;
                else
                    -- Predict not taken (PC+4)
                    pc_reg <= add_bv(pc_reg, to_bv(4, ADDR_WIDTH));
                end if;
            end if;
        end if;
    end process;

    -- Combinational prediction output
    process(pc_reg, btb)
        variable btb_idx : integer;
        variable tag : bit_vector(ADDR_WIDTH-BTB_BITS-3 downto 0);
    begin
        btb_idx := to_int(pc_reg(BTB_BITS+1 downto 2));
        tag := pc_reg(ADDR_WIDTH-1 downto BTB_BITS+2);

        if btb(btb_idx).valid = '1' and
           btb(btb_idx).tag = tag and
           btb(btb_idx).taken(1) = '1' then
            predicted_pc <= btb(btb_idx).target;
            prediction <= '1';
        else
            predicted_pc <= add_bv(pc_reg, to_bv(4, ADDR_WIDTH));
            prediction <= '0';
        end if;
    end process;

    pc_out <= pc_reg;
end Behavioral;

-- ============================================================================
-- Return Address Stack (RAS)
-- ============================================================================
entity ReturnAddressStack is
    generic (
        ADDR_WIDTH : integer := 32;
        STACK_SIZE : integer := 8
    );
    port (
        clk        : in  bit;
        reset      : in  bit;
        -- Push (on CALL/JAL)
        push       : in  bit;
        push_addr  : in  bit_vector(ADDR_WIDTH-1 downto 0);
        -- Pop (on RET/JALR to ra)
        pop        : in  bit;
        pop_addr   : out bit_vector(ADDR_WIDTH-1 downto 0);
        -- Status
        empty      : out bit;
        full       : out bit
    );
end ReturnAddressStack;

architecture Behavioral of ReturnAddressStack is
    type stack_array is array (0 to STACK_SIZE-1) of bit_vector(ADDR_WIDTH-1 downto 0);
    signal stack : stack_array;
    signal sp : integer range 0 to STACK_SIZE;
begin
    process(clk, reset)
    begin
        if reset = '1' then
            sp <= 0;
            for i in 0 to STACK_SIZE-1 loop
                stack(i) <= (others => '0');
            end loop;
        elsif clk'event and clk = '1' then
            if push = '1' and sp < STACK_SIZE then
                stack(sp) <= push_addr;
                sp <= sp + 1;
            elsif pop = '1' and sp > 0 then
                sp <= sp - 1;
            end if;
        end if;
    end process;

    pop_addr <= stack(sp-1) when sp > 0 else (others => '0');
    empty <= '1' when sp = 0 else '0';
    full <= '1' when sp = STACK_SIZE else '0';
end Behavioral;

-- ============================================================================
-- Branch Predictor (2-bit Bimodal)
-- ============================================================================
entity BimodalPredictor is
    generic (
        TABLE_SIZE : integer := 256;
        ADDR_WIDTH : integer := 32
    );
    port (
        clk          : in  bit;
        reset        : in  bit;
        -- Prediction interface
        pred_pc      : in  bit_vector(ADDR_WIDTH-1 downto 0);
        prediction   : out bit;
        -- Update interface
        update_en    : in  bit;
        update_pc    : in  bit_vector(ADDR_WIDTH-1 downto 0);
        actual_taken : in  bit
    );
end BimodalPredictor;

architecture Behavioral of BimodalPredictor is
    type counter_array is array (0 to TABLE_SIZE-1) of bit_vector(1 downto 0);
    signal counters : counter_array;

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
    -- Update counters
    process(clk, reset)
        variable idx : integer;
        variable cnt : integer;
    begin
        if reset = '1' then
            for i in 0 to TABLE_SIZE-1 loop
                counters(i) <= "01";  -- Weakly not taken
            end loop;
        elsif clk'event and clk = '1' then
            if update_en = '1' then
                idx := to_int(update_pc(9 downto 2)) mod TABLE_SIZE;
                cnt := to_int(counters(idx));

                if actual_taken = '1' then
                    if cnt < 3 then cnt := cnt + 1; end if;
                else
                    if cnt > 0 then cnt := cnt - 1; end if;
                end if;

                counters(idx) <= to_bv(cnt, 2);
            end if;
        end if;
    end process;

    -- Prediction output
    process(pred_pc, counters)
        variable idx : integer;
    begin
        idx := to_int(pred_pc(9 downto 2)) mod TABLE_SIZE;
        prediction <= counters(idx)(1);  -- MSB of counter
    end process;
end Behavioral;
