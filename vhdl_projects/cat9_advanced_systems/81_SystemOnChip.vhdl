-- ============================================================================
-- VHDL Project: SystemOnChip
-- Category: Advanced Systems
-- Complexity: Very High
-- Description: Simple System-on-Chip integrating CPU, memory, and peripherals
--              Bus interconnect, memory-mapped I/O
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity SystemOnChip is
    port (
        clk          : in  bit;
        reset        : in  bit;
        -- External GPIO
        gpio_in      : in  bit_vector(7 downto 0);
        gpio_out     : out bit_vector(7 downto 0);
        gpio_dir     : out bit_vector(7 downto 0);
        -- UART interface
        uart_rx      : in  bit;
        uart_tx      : out bit;
        -- SPI interface
        spi_mosi     : out bit;
        spi_miso     : in  bit;
        spi_sck      : out bit;
        spi_cs       : out bit;
        -- Interrupt
        ext_irq      : in  bit;
        -- Debug
        debug_halted : out bit
    );
end SystemOnChip;

architecture Behavioral of SystemOnChip is
    constant ADDR_WIDTH : integer := 16;
    constant DATA_WIDTH : integer := 16;

    -- Memory map
    -- 0x0000 - 0x0FFF : Boot ROM (4KB)
    -- 0x1000 - 0x1FFF : RAM (4KB)
    -- 0x2000 - 0x2FFF : Peripheral registers
    --   0x2000 : GPIO data
    --   0x2001 : GPIO direction
    --   0x2002 : UART data
    --   0x2003 : UART status
    --   0x2004 : Timer count
    --   0x2005 : Timer control
    --   0x2006 : Interrupt status
    --   0x2007 : Interrupt enable

    -- CPU bus signals
    signal cpu_addr : bit_vector(ADDR_WIDTH-1 downto 0);
    signal cpu_wdata : bit_vector(DATA_WIDTH-1 downto 0);
    signal cpu_rdata : bit_vector(DATA_WIDTH-1 downto 0);
    signal cpu_we : bit;
    signal cpu_re : bit;

    -- ROM signals
    signal rom_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal rom_sel : bit;

    -- RAM signals
    signal ram_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal ram_sel : bit;
    signal ram_we : bit;

    -- Peripheral signals
    signal periph_data : bit_vector(DATA_WIDTH-1 downto 0);
    signal periph_sel : bit;

    -- Internal registers
    signal gpio_out_reg : bit_vector(7 downto 0);
    signal gpio_dir_reg : bit_vector(7 downto 0);
    signal timer_count : bit_vector(15 downto 0);
    signal timer_ctrl : bit_vector(7 downto 0);
    signal irq_status : bit_vector(7 downto 0);
    signal irq_enable : bit_vector(7 downto 0);

    -- RAM array
    type ram_array is array (0 to 4095) of bit_vector(DATA_WIDTH-1 downto 0);
    signal ram : ram_array;

    -- Boot ROM (simple program)
    type rom_array is array (0 to 255) of bit_vector(DATA_WIDTH-1 downto 0);
    constant boot_rom : rom_array := (
        -- Initialize GPIO
        0  => "0001001000000000",  -- LDA #0xFF
        1  => "0011001000000001",  -- STA 0x2001 (GPIO dir = output)
        2  => "0001000001010101",  -- LDA #0x55
        3  => "0011001000000000",  -- STA 0x2000 (GPIO data)
        -- Main loop
        4  => "0010001000000000",  -- LDM 0x2000 (read GPIO)
        5  => "1100000000000100",  -- INC
        6  => "0011001000000000",  -- STA 0x2000
        7  => "1000000000000100",  -- JMP 4
        others => (others => '0')
    );

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
    -- Address decoding
    rom_sel <= '1' when cpu_addr(15 downto 12) = "0000" else '0';
    ram_sel <= '1' when cpu_addr(15 downto 12) = "0001" else '0';
    periph_sel <= '1' when cpu_addr(15 downto 12) = "0010" else '0';

    ram_we <= cpu_we and ram_sel;

    -- ROM read
    rom_data <= boot_rom(to_int(cpu_addr(7 downto 0)));

    -- RAM read/write
    process(clk)
    begin
        if clk'event and clk = '1' then
            if ram_we = '1' then
                ram(to_int(cpu_addr(11 downto 0))) <= cpu_wdata;
            end if;
        end if;
    end process;

    ram_data <= ram(to_int(cpu_addr(11 downto 0)));

    -- Peripheral registers
    periph_proc: process(clk, reset)
    begin
        if reset = '1' then
            gpio_out_reg <= (others => '0');
            gpio_dir_reg <= (others => '0');
            timer_count <= (others => '0');
            timer_ctrl <= (others => '0');
            irq_status <= (others => '0');
            irq_enable <= (others => '0');
        elsif clk'event and clk = '1' then
            -- Timer
            if timer_ctrl(0) = '1' then
                timer_count <= to_bv(to_int(timer_count) + 1, 16);
                if timer_count = (timer_count'range => '1') then
                    irq_status(0) <= '1';  -- Timer interrupt
                end if;
            end if;

            -- External interrupt
            if ext_irq = '1' then
                irq_status(1) <= '1';
            end if;

            -- Peripheral writes
            if periph_sel = '1' and cpu_we = '1' then
                case cpu_addr(3 downto 0) is
                    when "0000" => gpio_out_reg <= cpu_wdata(7 downto 0);
                    when "0001" => gpio_dir_reg <= cpu_wdata(7 downto 0);
                    when "0100" => timer_count <= cpu_wdata;
                    when "0101" => timer_ctrl <= cpu_wdata(7 downto 0);
                    when "0110" => irq_status <= cpu_wdata(7 downto 0);  -- Write to clear
                    when "0111" => irq_enable <= cpu_wdata(7 downto 0);
                    when others => null;
                end case;
            end if;
        end if;
    end process;

    -- Peripheral read mux
    process(cpu_addr, gpio_in, gpio_out_reg, gpio_dir_reg, timer_count, timer_ctrl, irq_status, irq_enable)
    begin
        periph_data <= (others => '0');
        case cpu_addr(3 downto 0) is
            when "0000" => periph_data(7 downto 0) <= gpio_in;
            when "0001" => periph_data(7 downto 0) <= gpio_dir_reg;
            when "0100" => periph_data <= timer_count;
            when "0101" => periph_data(7 downto 0) <= timer_ctrl;
            when "0110" => periph_data(7 downto 0) <= irq_status;
            when "0111" => periph_data(7 downto 0) <= irq_enable;
            when others => null;
        end case;
    end process;

    -- Bus read data mux
    cpu_rdata <= rom_data when rom_sel = '1' else
                 ram_data when ram_sel = '1' else
                 periph_data when periph_sel = '1' else
                 (others => '0');

    -- Simple CPU instance (inline for demonstration)
    -- In practice, this would instantiate the SimpleCPU component
    cpu_inst: process(clk, reset)
        type cpu_state is (FETCH, DECODE, EXECUTE, MEMORY, WRITEBACK, HALTED);
        variable state : cpu_state;
        variable pc : integer range 0 to 65535;
        variable ir : bit_vector(15 downto 0);
        variable acc : bit_vector(15 downto 0);
        variable mar : integer range 0 to 65535;
        variable opcode : bit_vector(3 downto 0);
        variable operand : bit_vector(11 downto 0);
    begin
        if reset = '1' then
            state := FETCH;
            pc := 0;
            ir := (others => '0');
            acc := (others => '0');
            mar := 0;
            cpu_we <= '0';
            cpu_re <= '0';
            debug_halted <= '0';
        elsif clk'event and clk = '1' then
            cpu_we <= '0';
            cpu_re <= '0';

            case state is
                when FETCH =>
                    cpu_addr <= to_bv(pc, ADDR_WIDTH);
                    cpu_re <= '1';
                    state := DECODE;

                when DECODE =>
                    ir := cpu_rdata;
                    opcode := ir(15 downto 12);
                    operand := ir(11 downto 0);
                    pc := pc + 1;

                    case opcode is
                        when "0001" =>  -- LDA immediate
                            acc := "0000" & operand;
                            state := FETCH;
                        when "0010" =>  -- LDM
                            mar := to_int(operand);
                            state := MEMORY;
                        when "0011" =>  -- STA
                            mar := to_int(operand);
                            cpu_addr <= to_bv(to_int(operand), ADDR_WIDTH);
                            cpu_wdata <= acc;
                            cpu_we <= '1';
                            state := WRITEBACK;
                        when "1000" =>  -- JMP
                            pc := to_int(operand);
                            state := FETCH;
                        when "1100" =>  -- INC
                            acc := to_bv(to_int(acc) + 1, 16);
                            state := FETCH;
                        when "1111" =>  -- HLT
                            debug_halted <= '1';
                            state := HALTED;
                        when others =>
                            state := FETCH;
                    end case;

                when MEMORY =>
                    cpu_addr <= to_bv(mar, ADDR_WIDTH);
                    cpu_re <= '1';
                    state := EXECUTE;

                when EXECUTE =>
                    acc := cpu_rdata;
                    state := FETCH;

                when WRITEBACK =>
                    state := FETCH;

                when HALTED =>
                    null;
            end case;
        end if;
    end process;

    -- Output assignments
    gpio_out <= gpio_out_reg;
    gpio_dir <= gpio_dir_reg;

    -- Stub outputs for unused interfaces
    uart_tx <= '1';
    spi_mosi <= '0';
    spi_sck <= '0';
    spi_cs <= '1';
end Behavioral;
