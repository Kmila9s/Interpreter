-- Test 41: FPGA Killer — deliberately maximizes the resource estimator's
-- counters without triggering pathological parser paths. Goal: a fixture
-- that *would* exceed a small FPGA's budget (high FF / LUT / IO / BRAM
-- numbers in the synthesized report) but stays well-bounded in source
-- size so the host process memory stays modest.
--
-- Curriculum: combines several units — wide register file (D.S.D Unit 1),
-- counters (Unit 3), memory blocks (Unit 4), ASM datapath (Unit 5),
-- and SoC interrupts (S.eC Unit 2.2).

library IEEE;
use IEEE.std_logic_1164.all;
use IEEE.numeric_std.all;

entity FPGA_Killer is
    generic (
        DATA_W : integer := 64;
        REGS   : integer := 32;
        BRAM_W : integer := 1024
    );
    port (
        clk     : in  std_logic;
        rst     : in  std_logic;
        en      : in  std_logic;
        op      : in  std_logic_vector(7 downto 0);
        rs1     : in  std_logic_vector(4 downto 0);
        rs2     : in  std_logic_vector(4 downto 0);
        rd      : in  std_logic_vector(4 downto 0);
        imm     : in  std_logic_vector(DATA_W - 1 downto 0);
        bus_in  : in  std_logic_vector(DATA_W - 1 downto 0);
        bus_out : out std_logic_vector(DATA_W - 1 downto 0);
        irq     : out std_logic_vector(15 downto 0);
        ready   : out std_logic
    );
end FPGA_Killer;

architecture RTL of FPGA_Killer is
    -- 32 x 64-bit register file = 2048 FFs.
    signal rf : std_logic_vector(REGS * DATA_W - 1 downto 0);

    -- 1 KB block-RAM-style storage = 8192 bits.
    signal bram : std_logic_vector(BRAM_W * 8 - 1 downto 0);

    -- Pipeline stages (each 64 FFs).
    signal pipe1, pipe2, pipe3, pipe4 : std_logic_vector(DATA_W - 1 downto 0) := (others => '0');

    -- Wide accumulator (128 bits).
    signal acc : std_logic_vector(2 * DATA_W - 1 downto 0) := (others => '0');

    -- IRQ pending vector (16 bits).
    signal irq_pending : std_logic_vector(15 downto 0) := (others => '0');

    -- Counters (16 of them, 32 bits each = 512 FFs).
    signal cnt_lo : std_logic_vector(255 downto 0) := (others => '0');
    signal cnt_hi : std_logic_vector(255 downto 0) := (others => '0');

    signal a_val, b_val, alu_out : std_logic_vector(DATA_W - 1 downto 0);
    signal ready_int : std_logic := '0';
begin
    -- Source operands from register file (single-line — the line splitter
    -- in the Interpreter doesn't join continuation lines).
    a_val <= rf((to_integer(unsigned(rs1)) + 1) * DATA_W - 1 downto to_integer(unsigned(rs1)) * DATA_W);
    b_val <= rf((to_integer(unsigned(rs2)) + 1) * DATA_W - 1 downto to_integer(unsigned(rs2)) * DATA_W);

    -- ALU (combinational, wide)
    alu_proc : process(op, a_val, b_val, imm)
    begin
        case op(3 downto 0) is
            when "0000" => alu_out <= std_logic_vector(unsigned(a_val) + unsigned(b_val));
            when "0001" => alu_out <= std_logic_vector(unsigned(a_val) - unsigned(b_val));
            when "0010" => alu_out <= a_val and b_val;
            when "0011" => alu_out <= a_val or  b_val;
            when "0100" => alu_out <= a_val xor b_val;
            when "0101" => alu_out <= std_logic_vector(unsigned(a_val) + unsigned(imm));
            when "0110" => alu_out <= a_val(DATA_W - 2 downto 0) & '0';
            when "0111" => alu_out <= '0' & a_val(DATA_W - 1 downto 1);
            when "1000" => alu_out <= imm;
            when "1001" => alu_out <= not a_val;
            when others => alu_out <= (others => '0');
        end case;
    end process alu_proc;

    -- Pipeline + register-file write-back
    pipeline_proc : process(clk, rst)
    begin
        if rst = '1' then
            pipe1 <= (others => '0');
            pipe2 <= (others => '0');
            pipe3 <= (others => '0');
            pipe4 <= (others => '0');
            ready_int <= '0';
        elsif rising_edge(clk) then
            if en = '1' then
                pipe1 <= alu_out;
                pipe2 <= pipe1;
                pipe3 <= pipe2;
                pipe4 <= pipe3;
                ready_int <= '1';
                rf((to_integer(unsigned(rd)) + 1) * DATA_W - 1 downto to_integer(unsigned(rd)) * DATA_W) <= alu_out;
            else
                ready_int <= '0';
            end if;
        end if;
    end process pipeline_proc;

    -- Wide multiply-accumulate
    mac_proc : process(clk, rst)
    begin
        if rst = '1' then
            acc <= (others => '0');
        elsif rising_edge(clk) then
            if en = '1' and op(7 downto 4) = "1010" then
                acc <= std_logic_vector(unsigned(acc) + (unsigned(a_val) * unsigned(b_val)));
            end if;
        end if;
    end process mac_proc;

    -- BRAM port (write on op="11000000", read otherwise)
    bram_proc : process(clk)
        variable addr_idx : integer;
    begin
        if rising_edge(clk) then
            addr_idx := to_integer(unsigned(rs1)) mod BRAM_W;
            if op = "11000000" then
                bram((addr_idx + 1) * 8 - 1 downto addr_idx * 8) <= bus_in(7 downto 0);
            end if;
        end if;
    end process bram_proc;

    -- 16 free-running counters (low + high words for big visible counts)
    counter_proc : process(clk, rst)
    begin
        if rst = '1' then
            cnt_lo <= (others => '0');
            cnt_hi <= (others => '0');
        elsif rising_edge(clk) then
            for i in 0 to 15 loop
                cnt_lo((i + 1) * 16 - 1 downto i * 16) <= std_logic_vector(unsigned(cnt_lo((i + 1) * 16 - 1 downto i * 16)) + 1);
                if cnt_lo((i + 1) * 16 - 1 downto i * 16) = x"FFFF" then
                    cnt_hi((i + 1) * 16 - 1 downto i * 16) <= std_logic_vector(unsigned(cnt_hi((i + 1) * 16 - 1 downto i * 16)) + 1);
                end if;
            end loop;
        end if;
    end process counter_proc;

    -- IRQ pending tracker
    irq_proc : process(clk, rst)
    begin
        if rst = '1' then
            irq_pending <= (others => '0');
        elsif rising_edge(clk) then
            for i in 0 to 15 loop
                if cnt_lo((i + 1) * 16 - 1 downto i * 16) = x"0000" then
                    irq_pending(i) <= '1';
                end if;
            end loop;
        end if;
    end process irq_proc;

    bus_out <= pipe4;
    irq     <= irq_pending;
    ready   <= ready_int;
end RTL;
