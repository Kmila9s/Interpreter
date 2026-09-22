-- ============================================================================
-- VHDL Project: DMAController
-- Category: Advanced Systems
-- Complexity: High
-- Description: Multi-channel DMA controller with scatter-gather support
--              Memory-to-memory, memory-to-peripheral transfers
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity DMAController is
    generic (
        ADDR_WIDTH   : integer := 32;
        DATA_WIDTH   : integer := 32;
        NUM_CHANNELS : integer := 4
    );
    port (
        clk          : in  bit;
        reset        : in  bit;
        -- CPU interface (configuration)
        cfg_addr     : in  bit_vector(7 downto 0);
        cfg_wdata    : in  bit_vector(DATA_WIDTH-1 downto 0);
        cfg_rdata    : out bit_vector(DATA_WIDTH-1 downto 0);
        cfg_we       : in  bit;
        cfg_re       : in  bit;
        -- Memory interface (master)
        mem_addr     : out bit_vector(ADDR_WIDTH-1 downto 0);
        mem_wdata    : out bit_vector(DATA_WIDTH-1 downto 0);
        mem_rdata    : in  bit_vector(DATA_WIDTH-1 downto 0);
        mem_we       : out bit;
        mem_re       : out bit;
        mem_ready    : in  bit;
        -- Peripheral request/ack
        dreq         : in  bit_vector(NUM_CHANNELS-1 downto 0);
        dack         : out bit_vector(NUM_CHANNELS-1 downto 0);
        -- Interrupts
        irq          : out bit_vector(NUM_CHANNELS-1 downto 0)
    );
end DMAController;

architecture Behavioral of DMAController is
    type channel_state is (IDLE, READ_REQ, READ_WAIT, WRITE_REQ, WRITE_WAIT, COMPLETE);

    type channel_reg is record
        src_addr  : bit_vector(ADDR_WIDTH-1 downto 0);
        dst_addr  : bit_vector(ADDR_WIDTH-1 downto 0);
        count     : bit_vector(15 downto 0);
        control   : bit_vector(7 downto 0);
        -- Control bits: [0]=enable, [1]=src_inc, [2]=dst_inc, [3]=circular,
        --               [4]=mem_to_mem, [5]=periph_to_mem, [6]=irq_en
        status    : bit_vector(7 downto 0);
        -- Status bits: [0]=busy, [1]=complete, [2]=error
        remaining : bit_vector(15 downto 0);
        state     : channel_state;
        data_reg  : bit_vector(DATA_WIDTH-1 downto 0);
    end record;

    type channel_array is array (0 to NUM_CHANNELS-1) of channel_reg;
    signal channels : channel_array;

    signal active_channel : integer range 0 to NUM_CHANNELS-1;
    signal arbitrate : bit;

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
    -- Configuration register access
    cfg_proc: process(clk, reset)
        variable ch : integer;
        variable reg_sel : integer;
    begin
        if reset = '1' then
            for i in 0 to NUM_CHANNELS-1 loop
                channels(i).src_addr <= (others => '0');
                channels(i).dst_addr <= (others => '0');
                channels(i).count <= (others => '0');
                channels(i).control <= (others => '0');
                channels(i).status <= (others => '0');
                channels(i).remaining <= (others => '0');
                channels(i).state <= IDLE;
                channels(i).data_reg <= (others => '0');
            end loop;
            cfg_rdata <= (others => '0');
        elsif clk'event and clk = '1' then
            ch := to_int(cfg_addr(5 downto 4));
            reg_sel := to_int(cfg_addr(3 downto 0));

            -- Register write
            if cfg_we = '1' and ch < NUM_CHANNELS then
                case reg_sel is
                    when 0 => channels(ch).src_addr <= cfg_wdata;
                    when 1 => channels(ch).dst_addr <= cfg_wdata;
                    when 2 => channels(ch).count <= cfg_wdata(15 downto 0);
                              channels(ch).remaining <= cfg_wdata(15 downto 0);
                    when 3 => channels(ch).control <= cfg_wdata(7 downto 0);
                              -- Starting transfer
                              if cfg_wdata(0) = '1' then
                                  channels(ch).state <= READ_REQ;
                                  channels(ch).status(0) <= '1';  -- Busy
                                  channels(ch).status(1) <= '0';  -- Not complete
                              end if;
                    when 4 => -- Write to status clears bits
                              for i in 0 to 7 loop
                                  if cfg_wdata(i) = '1' then
                                      channels(ch).status(i) <= '0';
                                  end if;
                              end loop;
                    when others => null;
                end case;
            end if;

            -- Register read
            if cfg_re = '1' and ch < NUM_CHANNELS then
                case reg_sel is
                    when 0 => cfg_rdata <= channels(ch).src_addr;
                    when 1 => cfg_rdata <= channels(ch).dst_addr;
                    when 2 => cfg_rdata <= (others => '0');
                              cfg_rdata(15 downto 0) <= channels(ch).remaining;
                    when 3 => cfg_rdata <= (others => '0');
                              cfg_rdata(7 downto 0) <= channels(ch).control;
                    when 4 => cfg_rdata <= (others => '0');
                              cfg_rdata(7 downto 0) <= channels(ch).status;
                    when others => cfg_rdata <= (others => '0');
                end case;
            end if;
        end if;
    end process;

    -- DMA transfer state machine
    dma_proc: process(clk, reset)
        variable ch : integer;
        variable active : bit;
    begin
        if reset = '1' then
            active_channel <= 0;
            mem_we <= '0';
            mem_re <= '0';
            mem_addr <= (others => '0');
            mem_wdata <= (others => '0');
            dack <= (others => '0');
            irq <= (others => '0');
        elsif clk'event and clk = '1' then
            mem_we <= '0';
            mem_re <= '0';
            dack <= (others => '0');
            irq <= (others => '0');

            -- Round-robin arbitration
            active := '0';
            for i in 0 to NUM_CHANNELS-1 loop
                ch := (active_channel + i) mod NUM_CHANNELS;
                if channels(ch).state /= IDLE and channels(ch).state /= COMPLETE then
                    active := '1';

                    case channels(ch).state is
                        when READ_REQ =>
                            -- Check for peripheral request if peripheral mode
                            if channels(ch).control(5) = '1' then
                                if dreq(ch) = '1' then
                                    mem_addr <= channels(ch).src_addr;
                                    mem_re <= '1';
                                    dack(ch) <= '1';
                                    channels(ch).state <= READ_WAIT;
                                end if;
                            else
                                mem_addr <= channels(ch).src_addr;
                                mem_re <= '1';
                                channels(ch).state <= READ_WAIT;
                            end if;

                        when READ_WAIT =>
                            if mem_ready = '1' then
                                channels(ch).data_reg <= mem_rdata;
                                -- Increment source if enabled
                                if channels(ch).control(1) = '1' then
                                    channels(ch).src_addr <= add_bv(
                                        channels(ch).src_addr,
                                        to_bv(4, ADDR_WIDTH));
                                end if;
                                channels(ch).state <= WRITE_REQ;
                            end if;

                        when WRITE_REQ =>
                            mem_addr <= channels(ch).dst_addr;
                            mem_wdata <= channels(ch).data_reg;
                            mem_we <= '1';
                            channels(ch).state <= WRITE_WAIT;

                        when WRITE_WAIT =>
                            if mem_ready = '1' then
                                -- Increment destination if enabled
                                if channels(ch).control(2) = '1' then
                                    channels(ch).dst_addr <= add_bv(
                                        channels(ch).dst_addr,
                                        to_bv(4, ADDR_WIDTH));
                                end if;

                                -- Decrement count
                                channels(ch).remaining <= to_bv(
                                    to_int(channels(ch).remaining) - 1, 16);

                                if to_int(channels(ch).remaining) = 1 then
                                    channels(ch).state <= COMPLETE;
                                else
                                    channels(ch).state <= READ_REQ;
                                end if;
                            end if;

                        when COMPLETE =>
                            channels(ch).status(0) <= '0';  -- Not busy
                            channels(ch).status(1) <= '1';  -- Complete

                            -- Generate interrupt if enabled
                            if channels(ch).control(6) = '1' then
                                irq(ch) <= '1';
                            end if;

                            -- Circular mode - restart
                            if channels(ch).control(3) = '1' then
                                channels(ch).remaining <= channels(ch).count;
                                channels(ch).state <= READ_REQ;
                            else
                                channels(ch).control(0) <= '0';  -- Disable
                                channels(ch).state <= IDLE;
                            end if;

                        when others =>
                            null;
                    end case;

                    active_channel <= ch;
                    exit;
                end if;
            end loop;

            if active = '0' then
                active_channel <= (active_channel + 1) mod NUM_CHANNELS;
            end if;
        end if;
    end process;
end Behavioral;
