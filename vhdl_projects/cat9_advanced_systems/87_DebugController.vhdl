-- ============================================================================
-- VHDL Project: DebugController
-- Category: Advanced Systems
-- Complexity: High
-- Description: On-chip debug controller with breakpoints, single-stepping
--              Trace buffer and register/memory access
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity DebugController is
    generic (
        ADDR_WIDTH      : integer := 32;
        DATA_WIDTH      : integer := 32;
        NUM_BREAKPOINTS : integer := 4;
        TRACE_DEPTH     : integer := 64
    );
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- CPU interface
        cpu_pc          : in  bit_vector(ADDR_WIDTH-1 downto 0);
        cpu_inst        : in  bit_vector(DATA_WIDTH-1 downto 0);
        cpu_executing   : in  bit;
        cpu_halt        : out bit;
        cpu_step        : out bit;
        cpu_resume      : out bit;
        -- Register access
        reg_addr        : out bit_vector(4 downto 0);
        reg_data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        reg_data_out    : out bit_vector(DATA_WIDTH-1 downto 0);
        reg_we          : out bit;
        -- Memory access
        mem_addr        : out bit_vector(ADDR_WIDTH-1 downto 0);
        mem_data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        mem_data_out    : out bit_vector(DATA_WIDTH-1 downto 0);
        mem_we          : out bit;
        mem_re          : out bit;
        -- External debug interface
        dbg_req         : in  bit;
        dbg_addr        : in  bit_vector(7 downto 0);
        dbg_wdata       : in  bit_vector(DATA_WIDTH-1 downto 0);
        dbg_rdata       : out bit_vector(DATA_WIDTH-1 downto 0);
        dbg_we          : in  bit;
        dbg_re          : in  bit;
        -- Status
        halted          : out bit;
        breakpoint_hit  : out bit
    );
end DebugController;

architecture Behavioral of DebugController is
    type state_type is (RUNNING, HALTED_STATE, STEPPING, REG_ACCESS, MEM_ACCESS);
    signal state : state_type;

    -- Breakpoint registers
    type bp_array is array (0 to NUM_BREAKPOINTS-1) of bit_vector(ADDR_WIDTH-1 downto 0);
    signal breakpoints : bp_array;
    signal bp_enable : bit_vector(NUM_BREAKPOINTS-1 downto 0);
    signal bp_hit : bit_vector(NUM_BREAKPOINTS-1 downto 0);

    -- Trace buffer
    type trace_entry is record
        pc   : bit_vector(ADDR_WIDTH-1 downto 0);
        inst : bit_vector(DATA_WIDTH-1 downto 0);
    end record;
    type trace_array is array (0 to TRACE_DEPTH-1) of trace_entry;
    signal trace_buffer : trace_array;
    signal trace_ptr : integer range 0 to TRACE_DEPTH-1;
    signal trace_enable : bit;
    signal trace_wrap : bit;

    -- Control registers
    signal ctrl_halt_req : bit;
    signal ctrl_step_req : bit;
    signal ctrl_resume_req : bit;

    -- Access buffers
    signal access_addr : bit_vector(ADDR_WIDTH-1 downto 0);
    signal access_data : bit_vector(DATA_WIDTH-1 downto 0);

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
    -- Breakpoint comparison
    bp_compare: process(cpu_pc, breakpoints, bp_enable, cpu_executing)
    begin
        bp_hit <= (others => '0');
        breakpoint_hit <= '0';

        for i in 0 to NUM_BREAKPOINTS-1 loop
            if bp_enable(i) = '1' and cpu_pc = breakpoints(i) and cpu_executing = '1' then
                bp_hit(i) <= '1';
                breakpoint_hit <= '1';
            end if;
        end loop;
    end process;

    -- Main debug state machine
    debug_fsm: process(clk, reset)
        variable addr : integer;
    begin
        if reset = '1' then
            state <= RUNNING;
            for i in 0 to NUM_BREAKPOINTS-1 loop
                breakpoints(i) <= (others => '0');
            end loop;
            bp_enable <= (others => '0');
            trace_ptr <= 0;
            trace_enable <= '0';
            trace_wrap <= '0';
            ctrl_halt_req <= '0';
            ctrl_step_req <= '0';
            ctrl_resume_req <= '0';
            cpu_halt <= '0';
            cpu_step <= '0';
            cpu_resume <= '0';
            halted <= '0';
            reg_we <= '0';
            mem_we <= '0';
            mem_re <= '0';
        elsif clk'event and clk = '1' then
            cpu_step <= '0';
            cpu_resume <= '0';
            reg_we <= '0';
            mem_we <= '0';
            mem_re <= '0';

            case state is
                when RUNNING =>
                    halted <= '0';
                    cpu_halt <= '0';

                    -- Trace recording
                    if trace_enable = '1' and cpu_executing = '1' then
                        trace_buffer(trace_ptr).pc <= cpu_pc;
                        trace_buffer(trace_ptr).inst <= cpu_inst;
                        if trace_ptr = TRACE_DEPTH - 1 then
                            trace_ptr <= 0;
                            trace_wrap <= '1';
                        else
                            trace_ptr <= trace_ptr + 1;
                        end if;
                    end if;

                    -- Check for halt conditions
                    if ctrl_halt_req = '1' or dbg_req = '1' then
                        cpu_halt <= '1';
                        state <= HALTED_STATE;
                        ctrl_halt_req <= '0';
                    elsif bp_hit /= (bp_hit'range => '0') then
                        cpu_halt <= '1';
                        state <= HALTED_STATE;
                    end if;

                when HALTED_STATE =>
                    halted <= '1';
                    cpu_halt <= '1';

                    if ctrl_step_req = '1' then
                        cpu_step <= '1';
                        ctrl_step_req <= '0';
                        state <= STEPPING;
                    elsif ctrl_resume_req = '1' then
                        cpu_resume <= '1';
                        cpu_halt <= '0';
                        ctrl_resume_req <= '0';
                        state <= RUNNING;
                    end if;

                when STEPPING =>
                    if cpu_executing = '1' then
                        state <= HALTED_STATE;
                    end if;

                when REG_ACCESS =>
                    state <= HALTED_STATE;

                when MEM_ACCESS =>
                    state <= HALTED_STATE;
            end case;

            -- External debug interface
            addr := to_int(dbg_addr);

            if dbg_we = '1' then
                case addr is
                    when 0 =>  -- Control register
                        ctrl_halt_req <= dbg_wdata(0);
                        ctrl_step_req <= dbg_wdata(1);
                        ctrl_resume_req <= dbg_wdata(2);
                        trace_enable <= dbg_wdata(4);

                    when 1 =>  -- Breakpoint enable
                        bp_enable <= dbg_wdata(NUM_BREAKPOINTS-1 downto 0);

                    when 16 to 19 =>  -- Breakpoint addresses
                        if addr - 16 < NUM_BREAKPOINTS then
                            breakpoints(addr - 16) <= dbg_wdata;
                        end if;

                    when 32 =>  -- Register access address
                        reg_addr <= dbg_wdata(4 downto 0);

                    when 33 =>  -- Register write data
                        reg_data_out <= dbg_wdata;
                        reg_we <= '1';
                        state <= REG_ACCESS;

                    when 48 =>  -- Memory access address
                        access_addr <= dbg_wdata;
                        mem_addr <= dbg_wdata;

                    when 49 =>  -- Memory write data
                        mem_data_out <= dbg_wdata;
                        mem_we <= '1';
                        state <= MEM_ACCESS;

                    when others =>
                        null;
                end case;
            end if;

            if dbg_re = '1' then
                case addr is
                    when 0 =>  -- Status register
                        dbg_rdata <= (others => '0');
                        if state = HALTED_STATE or state = STEPPING then
                            dbg_rdata(0) <= '1';
                        end if;
                        dbg_rdata(4) <= trace_enable;
                        dbg_rdata(5) <= trace_wrap;

                    when 1 =>  -- Breakpoint status
                        dbg_rdata <= (others => '0');
                        dbg_rdata(NUM_BREAKPOINTS-1 downto 0) <= bp_hit;

                    when 2 =>  -- Current PC
                        dbg_rdata <= cpu_pc;

                    when 3 =>  -- Current instruction
                        dbg_rdata <= cpu_inst;

                    when 4 =>  -- Trace pointer
                        dbg_rdata <= to_bv(trace_ptr, DATA_WIDTH);

                    when 16 to 19 =>  -- Breakpoint addresses
                        if addr - 16 < NUM_BREAKPOINTS then
                            dbg_rdata <= breakpoints(addr - 16);
                        end if;

                    when 34 =>  -- Register read data
                        dbg_rdata <= reg_data_in;

                    when 50 =>  -- Memory read
                        mem_re <= '1';
                        state <= MEM_ACCESS;

                    when 51 =>  -- Memory read data
                        dbg_rdata <= mem_data_in;

                    when 64 to 127 =>  -- Trace buffer read
                        if addr - 64 < TRACE_DEPTH then
                            dbg_rdata <= trace_buffer(addr - 64).pc;
                        end if;

                    when others =>
                        dbg_rdata <= (others => '0');
                end case;
            end if;
        end if;
    end process;
end Behavioral;
