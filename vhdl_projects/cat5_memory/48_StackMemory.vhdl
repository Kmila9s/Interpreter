-- ============================================================================
-- VHDL Project: StackMemory
-- Category: Memory Structures
-- Complexity: Medium
-- Description: Hardware stack implementation (LIFO)
--              Push, pop operations with overflow/underflow protection
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity StackMemory is
    generic (
        DATA_WIDTH : integer := 32;
        DEPTH      : integer := 64
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Operations
        push        : in  bit;
        pop         : in  bit;
        -- Data
        data_in     : in  bit_vector(DATA_WIDTH-1 downto 0);
        data_out    : out bit_vector(DATA_WIDTH-1 downto 0);
        -- Status
        empty       : out bit;
        full        : out bit;
        overflow    : out bit;
        underflow   : out bit;
        count       : out bit_vector(6 downto 0)
    );
end StackMemory;

architecture Behavioral of StackMemory is
    type stack_mem_type is array (0 to DEPTH-1) of bit_vector(DATA_WIDTH-1 downto 0);
    signal stack_mem : stack_mem_type;
    signal sp : integer range 0 to DEPTH;  -- Stack pointer

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
    stack_proc: process(clk, reset)
    begin
        if reset = '1' then
            sp <= 0;
            overflow <= '0';
            underflow <= '0';
            data_out <= (others => '0');
        elsif clk'event and clk = '1' then
            overflow <= '0';
            underflow <= '0';

            if push = '1' and pop = '0' then
                if sp < DEPTH then
                    stack_mem(sp) <= data_in;
                    sp <= sp + 1;
                else
                    overflow <= '1';
                end if;
            elsif pop = '1' and push = '0' then
                if sp > 0 then
                    sp <= sp - 1;
                    data_out <= stack_mem(sp - 1);
                else
                    underflow <= '1';
                end if;
            elsif push = '1' and pop = '1' then
                -- Simultaneous push/pop: replace top
                if sp > 0 then
                    stack_mem(sp - 1) <= data_in;
                    data_out <= stack_mem(sp - 1);
                else
                    stack_mem(0) <= data_in;
                    sp <= 1;
                end if;
            end if;
        end if;
    end process;

    empty <= '1' when sp = 0 else '0';
    full <= '1' when sp = DEPTH else '0';
    count <= to_bv(sp, 7);

end Behavioral;

-- ============================================================================
-- Call Stack (for subroutine returns)
-- ============================================================================
entity CallStack is
    generic (
        ADDR_WIDTH : integer := 32;
        DEPTH      : integer := 32
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Call: push return address
        call        : in  bit;
        call_addr   : in  bit_vector(ADDR_WIDTH-1 downto 0);
        -- Return: pop return address
        ret         : in  bit;
        ret_addr    : out bit_vector(ADDR_WIDTH-1 downto 0);
        -- Status
        empty       : out bit;
        full        : out bit;
        depth       : out bit_vector(5 downto 0)
    );
end CallStack;

architecture Behavioral of CallStack is
    type stack_type is array (0 to DEPTH-1) of bit_vector(ADDR_WIDTH-1 downto 0);
    signal stack : stack_type;
    signal sp : integer range 0 to DEPTH;

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
    begin
        if reset = '1' then
            sp <= 0;
            ret_addr <= (others => '0');
        elsif clk'event and clk = '1' then
            if call = '1' and sp < DEPTH then
                stack(sp) <= call_addr;
                sp <= sp + 1;
            elsif ret = '1' and sp > 0 then
                sp <= sp - 1;
                ret_addr <= stack(sp - 1);
            end if;
        end if;
    end process;

    empty <= '1' when sp = 0 else '0';
    full <= '1' when sp = DEPTH else '0';
    depth <= to_bv(sp, 6);

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity StackMemory_TB is
end StackMemory_TB;

architecture Test of StackMemory_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_push, tb_pop : bit;
    signal tb_data_in, tb_data_out : bit_vector(31 downto 0);
    signal tb_empty, tb_full, tb_overflow, tb_underflow : bit;
    signal tb_count : bit_vector(6 downto 0);
begin
    UUT: entity work.StackMemory
        generic map (DATA_WIDTH => 32, DEPTH => 64)
        port map (
            clk => tb_clk, reset => tb_reset,
            push => tb_push, pop => tb_pop,
            data_in => tb_data_in, data_out => tb_data_out,
            empty => tb_empty, full => tb_full,
            overflow => tb_overflow, underflow => tb_underflow,
            count => tb_count
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        tb_reset <= '1'; tb_push <= '0'; tb_pop <= '0';
        tb_data_in <= (others => '0');
        wait for 30 ns;

        tb_reset <= '0';

        -- Push 3 values
        tb_push <= '1';
        tb_data_in <= "00000000000000000000000000000001";
        wait for 10 ns;
        tb_data_in <= "00000000000000000000000000000010";
        wait for 10 ns;
        tb_data_in <= "00000000000000000000000000000011";
        wait for 10 ns;
        tb_push <= '0';

        -- Pop values (should come out in reverse)
        tb_pop <= '1';
        wait for 10 ns;
        wait for 10 ns;
        wait for 10 ns;
        tb_pop <= '0';

        -- Try underflow
        tb_pop <= '1';
        wait for 10 ns;
        tb_pop <= '0';

        wait for 50 ns;
        wait;
    end process;
end Test;
