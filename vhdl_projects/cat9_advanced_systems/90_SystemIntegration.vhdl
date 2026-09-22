-- ============================================================================
-- VHDL Project: SystemIntegration
-- Category: Advanced Systems
-- Complexity: Very High
-- Description: Complete integrated system with CPU, memory, peripherals
--              Boot ROM, GPIO, Timer, UART - full microcontroller
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity MicrocontrollerSystem is
    generic (
        CLK_FREQ     : integer := 50000000;
        ROM_SIZE     : integer := 1024;
        RAM_SIZE     : integer := 2048
    );
    port (
        clk          : in  bit;
        reset_n      : in  bit;
        -- GPIO
        gpio_port_a  : inout bit_vector(7 downto 0);
        -- UART
        uart_txd     : out bit;
        uart_rxd     : in  bit;
        -- LEDs and switches
        leds         : out bit_vector(3 downto 0);
        switches     : in  bit_vector(3 downto 0);
        -- External interrupt
        ext_int      : in  bit
    );
end MicrocontrollerSystem;

architecture Behavioral of MicrocontrollerSystem is
    -- Address map
    -- 0x0000-0x03FF: ROM (1KB)
    -- 0x0400-0x0BFF: RAM (2KB)
    -- 0x1000-0x10FF: GPIO
    -- 0x1100-0x11FF: Timer
    -- 0x1200-0x12FF: UART
    -- 0x1300-0x13FF: System control

    constant ADDR_WIDTH : integer := 16;
    constant DATA_WIDTH : integer := 8;

    -- Bus signals
    signal bus_addr : bit_vector(ADDR_WIDTH-1 downto 0);
    signal bus_wdata : bit_vector(DATA_WIDTH-1 downto 0);
    signal bus_rdata : bit_vector(DATA_WIDTH-1 downto 0);
    signal bus_we : bit;
    signal bus_re : bit;

    -- Chip select signals
    signal cs_rom : bit;
    signal cs_ram : bit;
    signal cs_gpio : bit;
    signal cs_timer : bit;
    signal cs_uart : bit;
    signal cs_sys : bit;

    -- Peripheral data outputs
    signal rom_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal ram_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal gpio_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal timer_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal uart_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal sys_data : bit_vector(DATA_WIDTH-1 downto 0);

    -- Internal signals
    signal reset : bit;
    signal irq : bit;
    signal timer_irq : bit;
    signal uart_rx_irq : bit;

    -- CPU signals
    signal cpu_halted : bit;

    -- GPIO registers
    signal gpio_out_reg : bit_vector(7 downto 0);
    signal gpio_dir_reg : bit_vector(7 downto 0);
    signal gpio_in_sync : bit_vector(7 downto 0);

    -- Timer registers
    signal timer_count : bit_vector(15 downto 0);
    signal timer_reload : bit_vector(15 downto 0);
    signal timer_ctrl : bit_vector(7 downto 0);

    -- UART registers
    signal uart_tx_data : bit_vector(7 downto 0);
    signal uart_rx_data : bit_vector(7 downto 0);
    signal uart_status : bit_vector(7 downto 0);
    signal uart_tx_busy : bit;
    signal uart_rx_valid : bit;

    -- ROM contents (simple test program)
    type rom_array is array (0 to ROM_SIZE-1) of bit_vector(DATA_WIDTH-1 downto 0);
    constant ROM : rom_array := (
        -- Initialize and blink LED
        0  => "00010000",  -- LDA #0x00
        1  => "00110000",  -- STA 0x1000 (GPIO out)
        2  => "00010001",  -- LDA #0x0F
        3  => "00110001",  -- STA 0x1001 (GPIO dir = output)
        -- Main loop
        4  => "00100000",  -- LDM 0x1000 (GPIO)
        5  => "11000000",  -- INC
        6  => "00110000",  -- STA 0x1000
        7  => "00010000",  -- LDA #delay_lo
        8  => "00110100",  -- STA timer_reload_lo
        9  => "00010100",  -- LDA #delay_hi
        10 => "00110101",  -- STA timer_reload_hi
        11 => "00010001",  -- LDA #1 (enable timer)
        12 => "00110110",  -- STA timer_ctrl
        -- Wait for timer
        13 => "00100110",  -- LDM timer_ctrl
        14 => "01100010",  -- AND #0x02 (check flag)
        15 => "10010000",  -- JZ wait
        16 => "10000100",  -- JMP main_loop (4)
        others => "00000000"
    );

    -- RAM
    type ram_array is array (0 to RAM_SIZE-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal RAM_MEM : ram_array;

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
    reset <= not reset_n;

    -- Address decoder
    cs_rom   <= '1' when bus_addr(15 downto 10) = "000000" else '0';
    cs_ram   <= '1' when bus_addr(15 downto 11) = "00001" else '0';
    cs_gpio  <= '1' when bus_addr(15 downto 8) = "00010000" else '0';
    cs_timer <= '1' when bus_addr(15 downto 8) = "00010001" else '0';
    cs_uart  <= '1' when bus_addr(15 downto 8) = "00010010" else '0';
    cs_sys   <= '1' when bus_addr(15 downto 8) = "00010011" else '0';

    -- ROM read
    rom_data <= ROM(to_int(bus_addr(9 downto 0))) when to_int(bus_addr(9 downto 0)) < ROM_SIZE
                else (others => '0');

    -- RAM read/write
    ram_proc: process(clk)
    begin
        if clk'event and clk = '1' then
            if cs_ram = '1' and bus_we = '1' then
                if to_int(bus_addr(10 downto 0)) < RAM_SIZE then
                    RAM_MEM(to_int(bus_addr(10 downto 0))) <= bus_wdata;
                end if;
            end if;
        end if;
    end process;

    ram_data <= RAM_MEM(to_int(bus_addr(10 downto 0)))
                when to_int(bus_addr(10 downto 0)) < RAM_SIZE
                else (others => '0');

    -- GPIO
    gpio_proc: process(clk, reset)
    begin
        if reset = '1' then
            gpio_out_reg <= (others => '0');
            gpio_dir_reg <= (others => '0');
            gpio_in_sync <= (others => '0');
        elsif clk'event and clk = '1' then
            -- Synchronize inputs
            -- gpio_in_sync <= gpio_port_a;  -- Would need inout handling

            if cs_gpio = '1' and bus_we = '1' then
                case bus_addr(1 downto 0) is
                    when "00" => gpio_out_reg <= bus_wdata;
                    when "01" => gpio_dir_reg <= bus_wdata;
                    when others => null;
                end case;
            end if;
        end if;
    end process;

    gpio_data <= gpio_in_sync when bus_addr(1 downto 0) = "00" else
                 gpio_dir_reg when bus_addr(1 downto 0) = "01" else
                 (others => '0');

    -- Output LEDs from low bits of GPIO
    leds <= gpio_out_reg(3 downto 0);

    -- Timer
    timer_proc: process(clk, reset)
    begin
        if reset = '1' then
            timer_count <= (others => '0');
            timer_reload <= (others => '0');
            timer_ctrl <= (others => '0');
            timer_irq <= '0';
        elsif clk'event and clk = '1' then
            timer_irq <= '0';

            -- Timer enabled
            if timer_ctrl(0) = '1' then
                if timer_count = (timer_count'range => '0') then
                    timer_count <= timer_reload;
                    timer_ctrl(1) <= '1';  -- Set flag
                    if timer_ctrl(2) = '1' then
                        timer_irq <= '1';
                    end if;
                else
                    timer_count <= to_bv(to_int(timer_count) - 1, 16);
                end if;
            end if;

            if cs_timer = '1' and bus_we = '1' then
                case bus_addr(2 downto 0) is
                    when "000" => timer_count(7 downto 0) <= bus_wdata;
                    when "001" => timer_count(15 downto 8) <= bus_wdata;
                    when "010" => timer_reload(7 downto 0) <= bus_wdata;
                    when "011" => timer_reload(15 downto 8) <= bus_wdata;
                    when "100" => timer_ctrl <= bus_wdata;
                                  -- Clear flag on write
                                  if bus_wdata(1) = '1' then
                                      timer_ctrl(1) <= '0';
                                  end if;
                    when others => null;
                end case;
            end if;
        end if;
    end process;

    timer_data <= timer_count(7 downto 0) when bus_addr(2 downto 0) = "000" else
                  timer_count(15 downto 8) when bus_addr(2 downto 0) = "001" else
                  timer_reload(7 downto 0) when bus_addr(2 downto 0) = "010" else
                  timer_reload(15 downto 8) when bus_addr(2 downto 0) = "011" else
                  timer_ctrl when bus_addr(2 downto 0) = "100" else
                  (others => '0');

    -- UART (simplified - just status/data registers)
    uart_proc: process(clk, reset)
    begin
        if reset = '1' then
            uart_tx_data <= (others => '0');
            uart_status <= "00000010";  -- TX ready
            uart_rx_data <= (others => '0');
        elsif clk'event and clk = '1' then
            if cs_uart = '1' and bus_we = '1' then
                case bus_addr(1 downto 0) is
                    when "00" => uart_tx_data <= bus_wdata;
                                 uart_status(1) <= '0';  -- TX busy
                    when "10" => uart_status <= bus_wdata;
                    when others => null;
                end case;
            end if;

            -- Simulate TX complete
            if uart_status(1) = '0' then
                uart_status(1) <= '1';  -- TX done
            end if;
        end if;
    end process;

    uart_data <= uart_rx_data when bus_addr(1 downto 0) = "01" else
                 uart_status when bus_addr(1 downto 0) = "10" else
                 (others => '0');

    uart_txd <= '1';  -- Idle

    -- System control
    sys_data <= "0000" & switches;

    -- Bus read multiplexer
    bus_rdata <= rom_data when cs_rom = '1' else
                 ram_data when cs_ram = '1' else
                 gpio_data when cs_gpio = '1' else
                 timer_data when cs_timer = '1' else
                 uart_data when cs_uart = '1' else
                 sys_data when cs_sys = '1' else
                 (others => '0');

    -- Interrupt logic
    irq <= timer_irq or ext_int;

    -- Simple CPU (inline for demonstration)
    cpu_inst: process(clk, reset)
        type cpu_state is (FETCH, DECODE, EXECUTE, MEMORY, WRITEBACK, HALTED);
        variable state : cpu_state;
        variable pc : integer range 0 to 65535;
        variable ir : bit_vector(7 downto 0);
        variable acc : bit_vector(7 downto 0);
        variable opcode : bit_vector(3 downto 0);
        variable operand : bit_vector(3 downto 0);
    begin
        if reset = '1' then
            state := FETCH;
            pc := 0;
            ir := (others => '0');
            acc := (others => '0');
            bus_we <= '0';
            bus_re <= '0';
            cpu_halted <= '0';
        elsif clk'event and clk = '1' then
            bus_we <= '0';
            bus_re <= '0';

            case state is
                when FETCH =>
                    bus_addr <= to_bv(pc, ADDR_WIDTH);
                    bus_re <= '1';
                    state := DECODE;

                when DECODE =>
                    ir := bus_rdata;
                    opcode := ir(7 downto 4);
                    operand := ir(3 downto 0);
                    pc := pc + 1;

                    case opcode is
                        when "0001" =>  -- LDA immediate
                            acc := "0000" & operand;
                            state := FETCH;
                        when "0010" =>  -- LDM
                            bus_addr <= "000000000001" & operand;
                            bus_re <= '1';
                            state := MEMORY;
                        when "0011" =>  -- STA
                            bus_addr <= "000000000001" & operand;
                            bus_wdata <= acc;
                            bus_we <= '1';
                            state := WRITEBACK;
                        when "0110" =>  -- AND
                            acc := acc and ("0000" & operand);
                            state := FETCH;
                        when "1000" =>  -- JMP
                            pc := to_int(operand);
                            state := FETCH;
                        when "1001" =>  -- JZ
                            if acc = "00000000" then
                                pc := to_int(operand);
                            end if;
                            state := FETCH;
                        when "1100" =>  -- INC
                            acc := to_bv((to_int(acc) + 1) mod 256, 8);
                            state := FETCH;
                        when "1111" =>  -- HLT
                            cpu_halted <= '1';
                            state := HALTED;
                        when others =>
                            state := FETCH;
                    end case;

                when MEMORY =>
                    state := EXECUTE;

                when EXECUTE =>
                    acc := bus_rdata;
                    state := FETCH;

                when WRITEBACK =>
                    state := FETCH;

                when HALTED =>
                    null;
            end case;
        end if;
    end process;
end Behavioral;
