-- ============================================================================
-- VHDL Project: VendingController
-- Category: State Machines
-- Complexity: Medium-High
-- Description: Vending machine controller with coin handling
--              Multiple products, change calculation, error handling
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity VendingController is
    port (
        clk             : in  bit;
        reset           : in  bit;
        -- Coin inputs (active high pulse)
        coin_5          : in  bit;  -- 5 cents
        coin_10         : in  bit;  -- 10 cents
        coin_25         : in  bit;  -- 25 cents
        coin_100        : in  bit;  -- 1 dollar
        -- Product selection (active high)
        select_a        : in  bit;  -- Product A: 75 cents
        select_b        : in  bit;  -- Product B: 100 cents
        select_c        : in  bit;  -- Product C: 125 cents
        select_d        : in  bit;  -- Product D: 150 cents
        -- Cancel button
        cancel          : in  bit;
        -- Outputs
        dispense_a      : out bit;
        dispense_b      : out bit;
        dispense_c      : out bit;
        dispense_d      : out bit;
        return_coins    : out bit;
        change_amount   : out bit_vector(7 downto 0);  -- Cents to return
        balance_display : out bit_vector(7 downto 0);  -- Current balance
        -- Status
        ready           : out bit;
        error_flag      : out bit;
        state_out       : out bit_vector(3 downto 0)
    );
end VendingController;

architecture Behavioral of VendingController is
    -- Product prices in cents
    constant PRICE_A : integer := 75;
    constant PRICE_B : integer := 100;
    constant PRICE_C : integer := 125;
    constant PRICE_D : integer := 150;
    constant MAX_BALANCE : integer := 255;

    -- States
    type state_type is (
        IDLE,           -- Waiting for coins or selection
        ACCEPTING,      -- Accepting coins
        SELECTED,       -- Product selected, checking balance
        DISPENSING,     -- Dispensing product
        CHANGE_RETURN,  -- Returning change
        REFUND,         -- Full refund (cancel)
        ERROR_STATE     -- Error condition
    );

    signal state : state_type;
    signal balance : integer range 0 to MAX_BALANCE;
    signal selected_price : integer range 0 to 200;
    signal selected_product : bit_vector(1 downto 0);
    signal change_to_return : integer range 0 to MAX_BALANCE;

    -- Timer for dispensing and change return
    signal timer : integer range 0 to 255;

    -- Edge detection for coin inputs
    signal coin_5_prev, coin_10_prev, coin_25_prev, coin_100_prev : bit;
    signal coin_5_edge, coin_10_edge, coin_25_edge, coin_100_edge : bit;

    function to_bv8(val : integer) return bit_vector is
        variable result : bit_vector(7 downto 0);
        variable temp : integer := val;
    begin
        for i in 0 to 7 loop
            if (temp mod 2) = 1 then
                result(i) := '1';
            else
                result(i) := '0';
            end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    -- Edge detection for coin inputs
    edge_detect: process(clk, reset)
    begin
        if reset = '1' then
            coin_5_prev <= '0';
            coin_10_prev <= '0';
            coin_25_prev <= '0';
            coin_100_prev <= '0';
        elsif clk'event and clk = '1' then
            coin_5_prev <= coin_5;
            coin_10_prev <= coin_10;
            coin_25_prev <= coin_25;
            coin_100_prev <= coin_100;
        end if;
    end process;

    coin_5_edge <= coin_5 and not coin_5_prev;
    coin_10_edge <= coin_10 and not coin_10_prev;
    coin_25_edge <= coin_25 and not coin_25_prev;
    coin_100_edge <= coin_100 and not coin_100_prev;

    -- Main FSM
    fsm_proc: process(clk, reset)
        variable new_balance : integer;
    begin
        if reset = '1' then
            state <= IDLE;
            balance <= 0;
            selected_price <= 0;
            selected_product <= "00";
            change_to_return <= 0;
            timer <= 0;
        elsif clk'event and clk = '1' then
            case state is
                when IDLE =>
                    timer <= 0;
                    change_to_return <= 0;

                    -- Accept coins
                    new_balance := balance;
                    if coin_5_edge = '1' then
                        new_balance := new_balance + 5;
                    end if;
                    if coin_10_edge = '1' then
                        new_balance := new_balance + 10;
                    end if;
                    if coin_25_edge = '1' then
                        new_balance := new_balance + 25;
                    end if;
                    if coin_100_edge = '1' then
                        new_balance := new_balance + 100;
                    end if;

                    if new_balance > MAX_BALANCE then
                        new_balance := MAX_BALANCE;
                    end if;
                    balance <= new_balance;

                    if new_balance > 0 then
                        state <= ACCEPTING;
                    end if;

                when ACCEPTING =>
                    -- Continue accepting coins
                    new_balance := balance;
                    if coin_5_edge = '1' then
                        new_balance := new_balance + 5;
                    end if;
                    if coin_10_edge = '1' then
                        new_balance := new_balance + 10;
                    end if;
                    if coin_25_edge = '1' then
                        new_balance := new_balance + 25;
                    end if;
                    if coin_100_edge = '1' then
                        new_balance := new_balance + 100;
                    end if;

                    if new_balance > MAX_BALANCE then
                        new_balance := MAX_BALANCE;
                    end if;
                    balance <= new_balance;

                    -- Check for product selection
                    if select_a = '1' then
                        selected_price <= PRICE_A;
                        selected_product <= "00";
                        state <= SELECTED;
                    elsif select_b = '1' then
                        selected_price <= PRICE_B;
                        selected_product <= "01";
                        state <= SELECTED;
                    elsif select_c = '1' then
                        selected_price <= PRICE_C;
                        selected_product <= "10";
                        state <= SELECTED;
                    elsif select_d = '1' then
                        selected_price <= PRICE_D;
                        selected_product <= "11";
                        state <= SELECTED;
                    elsif cancel = '1' then
                        state <= REFUND;
                    end if;

                when SELECTED =>
                    -- Check if sufficient balance
                    if balance >= selected_price then
                        change_to_return <= balance - selected_price;
                        balance <= 0;
                        state <= DISPENSING;
                        timer <= 0;
                    else
                        -- Not enough money, go back to accepting
                        state <= ACCEPTING;
                    end if;

                when DISPENSING =>
                    timer <= timer + 1;
                    if timer >= 10 then
                        if change_to_return > 0 then
                            state <= CHANGE_RETURN;
                            timer <= 0;
                        else
                            state <= IDLE;
                        end if;
                    end if;

                when CHANGE_RETURN =>
                    timer <= timer + 1;
                    if timer >= 10 then
                        change_to_return <= 0;
                        state <= IDLE;
                    end if;

                when REFUND =>
                    change_to_return <= balance;
                    balance <= 0;
                    timer <= timer + 1;
                    if timer >= 10 then
                        change_to_return <= 0;
                        state <= IDLE;
                    end if;

                when ERROR_STATE =>
                    if cancel = '1' then
                        state <= IDLE;
                        balance <= 0;
                    end if;

            end case;
        end if;
    end process;

    -- Output logic
    ready <= '1' when state = IDLE or state = ACCEPTING else '0';

    dispense_a <= '1' when state = DISPENSING and selected_product = "00" else '0';
    dispense_b <= '1' when state = DISPENSING and selected_product = "01" else '0';
    dispense_c <= '1' when state = DISPENSING and selected_product = "10" else '0';
    dispense_d <= '1' when state = DISPENSING and selected_product = "11" else '0';

    return_coins <= '1' when state = CHANGE_RETURN or state = REFUND else '0';
    change_amount <= to_bv8(change_to_return);
    balance_display <= to_bv8(balance);

    error_flag <= '1' when state = ERROR_STATE else '0';

    with state select state_out <=
        "0000" when IDLE,
        "0001" when ACCEPTING,
        "0010" when SELECTED,
        "0011" when DISPENSING,
        "0100" when CHANGE_RETURN,
        "0101" when REFUND,
        "1111" when ERROR_STATE;

end Behavioral;

-- ============================================================================
-- Inventory Tracking Module
-- ============================================================================
entity InventoryTracker is
    generic (
        NUM_PRODUCTS : integer := 4;
        MAX_STOCK    : integer := 20
    );
    port (
        clk         : in  bit;
        reset       : in  bit;
        -- Stock management
        product_id  : in  bit_vector(1 downto 0);
        dispense    : in  bit;  -- Decrement stock
        restock     : in  bit;  -- Increment stock
        restock_qty : in  bit_vector(4 downto 0);
        -- Status
        stock_level : out bit_vector(4 downto 0);
        out_of_stock: out bit;
        low_stock   : out bit;  -- Below 3
        all_empty   : out bit
    );
end InventoryTracker;

architecture Behavioral of InventoryTracker is
    type stock_array is array (0 to NUM_PRODUCTS-1) of integer range 0 to MAX_STOCK;
    signal stocks : stock_array;

    function to_integer(bv : bit_vector) return integer is
        variable result : integer := 0;
        variable power : integer := 1;
    begin
        for i in bv'low to bv'high loop
            if bv(i) = '1' then
                result := result + power;
            end if;
            power := power * 2;
        end loop;
        return result;
    end function;

    function to_bv5(val : integer) return bit_vector is
        variable result : bit_vector(4 downto 0);
        variable temp : integer := val;
    begin
        for i in 0 to 4 loop
            if (temp mod 2) = 1 then
                result(i) := '1';
            else
                result(i) := '0';
            end if;
            temp := temp / 2;
        end loop;
        return result;
    end function;

begin
    process(clk, reset)
        variable idx : integer;
        variable qty : integer;
    begin
        if reset = '1' then
            for i in 0 to NUM_PRODUCTS-1 loop
                stocks(i) <= MAX_STOCK;
            end loop;
        elsif clk'event and clk = '1' then
            idx := to_integer(product_id);

            if dispense = '1' and idx < NUM_PRODUCTS then
                if stocks(idx) > 0 then
                    stocks(idx) <= stocks(idx) - 1;
                end if;
            end if;

            if restock = '1' and idx < NUM_PRODUCTS then
                qty := to_integer(restock_qty);
                if stocks(idx) + qty <= MAX_STOCK then
                    stocks(idx) <= stocks(idx) + qty;
                else
                    stocks(idx) <= MAX_STOCK;
                end if;
            end if;
        end if;
    end process;

    -- Output current stock level
    process(product_id, stocks)
        variable idx : integer;
    begin
        idx := to_integer(product_id);
        if idx < NUM_PRODUCTS then
            stock_level <= to_bv5(stocks(idx));
            out_of_stock <= '1' when stocks(idx) = 0 else '0';
            low_stock <= '1' when stocks(idx) < 3 else '0';
        else
            stock_level <= (others => '0');
            out_of_stock <= '1';
            low_stock <= '1';
        end if;
    end process;

    -- Check if all products empty
    process(stocks)
        variable any_stock : bit;
    begin
        any_stock := '0';
        for i in 0 to NUM_PRODUCTS-1 loop
            if stocks(i) > 0 then
                any_stock := '1';
            end if;
        end loop;
        all_empty <= not any_stock;
    end process;

end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity VendingController_TB is
end VendingController_TB;

architecture Test of VendingController_TB is
    signal tb_clk, tb_reset : bit;
    signal tb_coin_5, tb_coin_10, tb_coin_25, tb_coin_100 : bit;
    signal tb_sel_a, tb_sel_b, tb_sel_c, tb_sel_d : bit;
    signal tb_cancel : bit;
    signal tb_disp_a, tb_disp_b, tb_disp_c, tb_disp_d : bit;
    signal tb_return_coins : bit;
    signal tb_change, tb_balance : bit_vector(7 downto 0);
    signal tb_ready, tb_error : bit;
    signal tb_state : bit_vector(3 downto 0);
begin
    UUT: entity work.VendingController
        port map (
            clk             => tb_clk,
            reset           => tb_reset,
            coin_5          => tb_coin_5,
            coin_10         => tb_coin_10,
            coin_25         => tb_coin_25,
            coin_100        => tb_coin_100,
            select_a        => tb_sel_a,
            select_b        => tb_sel_b,
            select_c        => tb_sel_c,
            select_d        => tb_sel_d,
            cancel          => tb_cancel,
            dispense_a      => tb_disp_a,
            dispense_b      => tb_disp_b,
            dispense_c      => tb_disp_c,
            dispense_d      => tb_disp_d,
            return_coins    => tb_return_coins,
            change_amount   => tb_change,
            balance_display => tb_balance,
            ready           => tb_ready,
            error_flag      => tb_error,
            state_out       => tb_state
        );

    clk_gen: process
    begin
        tb_clk <= '0'; wait for 5 ns;
        tb_clk <= '1'; wait for 5 ns;
    end process;

    stimulus: process
    begin
        -- Initialize
        tb_reset <= '1';
        tb_coin_5 <= '0'; tb_coin_10 <= '0';
        tb_coin_25 <= '0'; tb_coin_100 <= '0';
        tb_sel_a <= '0'; tb_sel_b <= '0';
        tb_sel_c <= '0'; tb_sel_d <= '0';
        tb_cancel <= '0';
        wait for 30 ns;

        tb_reset <= '0';

        -- Insert $1.00
        tb_coin_100 <= '1'; wait for 10 ns; tb_coin_100 <= '0'; wait for 10 ns;

        -- Select product A (75 cents) - should get 25 cents change
        tb_sel_a <= '1'; wait for 10 ns; tb_sel_a <= '0';

        -- Wait for dispensing
        wait for 200 ns;

        -- Insert 25 + 25 + 25 = 75 cents
        tb_coin_25 <= '1'; wait for 10 ns; tb_coin_25 <= '0'; wait for 10 ns;
        tb_coin_25 <= '1'; wait for 10 ns; tb_coin_25 <= '0'; wait for 10 ns;
        tb_coin_25 <= '1'; wait for 10 ns; tb_coin_25 <= '0'; wait for 10 ns;

        -- Select product A (exact change)
        tb_sel_a <= '1'; wait for 10 ns; tb_sel_a <= '0';

        wait for 200 ns;

        -- Insert coins then cancel
        tb_coin_25 <= '1'; wait for 10 ns; tb_coin_25 <= '0'; wait for 10 ns;
        tb_cancel <= '1'; wait for 10 ns; tb_cancel <= '0';

        wait for 200 ns;
        wait;
    end process;
end Test;
