-- ============================================================================
-- VHDL Project: InterruptController
-- Category: Advanced Systems
-- Complexity: High
-- Description: Programmable interrupt controller with priority encoding
--              Supports nested interrupts, edge/level triggering
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity InterruptController is
    generic (
        NUM_IRQS     : integer := 16;
        PRIORITY_BITS : integer := 4
    );
    port (
        clk          : in  bit;
        reset        : in  bit;
        -- Interrupt inputs
        irq_lines    : in  bit_vector(NUM_IRQS-1 downto 0);
        -- CPU interface
        irq_out      : out bit;
        irq_vector   : out bit_vector(7 downto 0);
        irq_ack      : in  bit;
        -- Configuration interface
        cfg_addr     : in  bit_vector(4 downto 0);
        cfg_wdata    : in  bit_vector(7 downto 0);
        cfg_rdata    : out bit_vector(7 downto 0);
        cfg_we       : in  bit
    );
end InterruptController;

architecture Behavioral of InterruptController is
    -- Interrupt pending register
    signal irq_pending : bit_vector(NUM_IRQS-1 downto 0);

    -- Interrupt mask register (1 = enabled)
    signal irq_mask : bit_vector(NUM_IRQS-1 downto 0);

    -- Interrupt in-service register
    signal irq_in_service : bit_vector(NUM_IRQS-1 downto 0);

    -- Priority registers (one per IRQ)
    type priority_array is array (0 to NUM_IRQS-1) of bit_vector(PRIORITY_BITS-1 downto 0);
    signal irq_priority : priority_array;

    -- Edge detection
    signal irq_prev : bit_vector(NUM_IRQS-1 downto 0);
    signal irq_edge_mode : bit_vector(NUM_IRQS-1 downto 0);  -- 1 = edge, 0 = level

    -- Current servicing priority
    signal current_priority : bit_vector(PRIORITY_BITS-1 downto 0);

    -- Highest pending priority
    signal highest_pending_irq : integer range 0 to NUM_IRQS-1;
    signal highest_pending_priority : bit_vector(PRIORITY_BITS-1 downto 0);
    signal pending_valid : bit;

    -- Interrupt vector base
    signal vector_base : bit_vector(7 downto 0);

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
    -- Edge detection and pending logic
    edge_detect: process(clk, reset)
    begin
        if reset = '1' then
            irq_prev <= (others => '0');
            irq_pending <= (others => '0');
        elsif clk'event and clk = '1' then
            irq_prev <= irq_lines;

            for i in 0 to NUM_IRQS-1 loop
                if irq_edge_mode(i) = '1' then
                    -- Edge triggered: detect rising edge
                    if irq_lines(i) = '1' and irq_prev(i) = '0' then
                        irq_pending(i) <= '1';
                    end if;
                else
                    -- Level triggered: reflect current level
                    irq_pending(i) <= irq_lines(i);
                end if;

                -- Clear pending on acknowledge (handled below)
            end loop;
        end if;
    end process;

    -- Priority encoder - find highest priority pending interrupt
    priority_encode: process(irq_pending, irq_mask, irq_in_service, irq_priority, current_priority)
        variable found : bit;
        variable best_irq : integer;
        variable best_priority : integer;
        variable this_priority : integer;
    begin
        found := '0';
        best_irq := 0;
        best_priority := 0;

        for i in 0 to NUM_IRQS-1 loop
            if irq_pending(i) = '1' and irq_mask(i) = '1' and irq_in_service(i) = '0' then
                this_priority := to_int(irq_priority(i));
                if this_priority > best_priority or found = '0' then
                    best_priority := this_priority;
                    best_irq := i;
                    found := '1';
                end if;
            end if;
        end loop;

        pending_valid <= found;
        highest_pending_irq <= best_irq;
        highest_pending_priority <= to_bv(best_priority, PRIORITY_BITS);
    end process;

    -- Interrupt output logic
    irq_output: process(pending_valid, highest_pending_priority, current_priority)
    begin
        if pending_valid = '1' and
           to_int(highest_pending_priority) > to_int(current_priority) then
            irq_out <= '1';
        else
            irq_out <= '0';
        end if;
    end process;

    -- Interrupt vector generation
    irq_vector <= to_bv(to_int(vector_base) + highest_pending_irq, 8);

    -- Acknowledge and configuration
    config_proc: process(clk, reset)
        variable addr : integer;
    begin
        if reset = '1' then
            irq_mask <= (others => '0');
            irq_in_service <= (others => '0');
            irq_edge_mode <= (others => '0');
            current_priority <= (others => '0');
            vector_base <= "00100000";  -- Default vector base 0x20
            for i in 0 to NUM_IRQS-1 loop
                irq_priority(i) <= to_bv(i, PRIORITY_BITS);  -- Default priority = IRQ number
            end loop;
            cfg_rdata <= (others => '0');
        elsif clk'event and clk = '1' then
            -- Acknowledge interrupt
            if irq_ack = '1' and pending_valid = '1' then
                irq_in_service(highest_pending_irq) <= '1';
                current_priority <= highest_pending_priority;
                -- Clear edge-triggered pending
                if irq_edge_mode(highest_pending_irq) = '1' then
                    irq_pending(highest_pending_irq) <= '0';
                end if;
            end if;

            -- Configuration writes
            addr := to_int(cfg_addr);
            if cfg_we = '1' then
                case addr is
                    when 0 =>  -- IRQ mask low
                        irq_mask(7 downto 0) <= cfg_wdata;
                    when 1 =>  -- IRQ mask high
                        irq_mask(15 downto 8) <= cfg_wdata;
                    when 2 =>  -- Edge mode low
                        irq_edge_mode(7 downto 0) <= cfg_wdata;
                    when 3 =>  -- Edge mode high
                        irq_edge_mode(15 downto 8) <= cfg_wdata;
                    when 4 =>  -- EOI (End Of Interrupt)
                        -- Clear in-service bit for specified IRQ
                        if to_int(cfg_wdata) < NUM_IRQS then
                            irq_in_service(to_int(cfg_wdata)) <= '0';
                        end if;
                        -- Update current priority (find next highest in-service)
                        current_priority <= (others => '0');
                        for i in 0 to NUM_IRQS-1 loop
                            if irq_in_service(i) = '1' and i /= to_int(cfg_wdata) then
                                if to_int(irq_priority(i)) > to_int(current_priority) then
                                    current_priority <= irq_priority(i);
                                end if;
                            end if;
                        end loop;
                    when 5 =>  -- Vector base
                        vector_base <= cfg_wdata;
                    when 16 to 31 =>  -- Priority registers
                        if addr - 16 < NUM_IRQS then
                            irq_priority(addr - 16) <= cfg_wdata(PRIORITY_BITS-1 downto 0);
                        end if;
                    when others =>
                        null;
                end case;
            end if;

            -- Configuration reads
            case addr is
                when 0 => cfg_rdata <= irq_mask(7 downto 0);
                when 1 => cfg_rdata <= irq_mask(15 downto 8);
                when 2 => cfg_rdata <= irq_edge_mode(7 downto 0);
                when 3 => cfg_rdata <= irq_edge_mode(15 downto 8);
                when 4 => cfg_rdata <= irq_pending(7 downto 0);
                when 5 => cfg_rdata <= irq_pending(15 downto 8);
                when 6 => cfg_rdata <= irq_in_service(7 downto 0);
                when 7 => cfg_rdata <= irq_in_service(15 downto 8);
                when 8 => cfg_rdata <= to_bv(highest_pending_irq, 8);
                when 9 => cfg_rdata <= vector_base;
                when 16 to 31 =>
                    if addr - 16 < NUM_IRQS then
                        cfg_rdata <= "0000" & irq_priority(addr - 16);
                    else
                        cfg_rdata <= (others => '0');
                    end if;
                when others =>
                    cfg_rdata <= (others => '0');
            end case;
        end if;
    end process;
end Behavioral;
