-- ============================================================================
-- VHDL Project: Decoder4to16
-- Category: Digital Logic Fundamentals
-- Complexity: Low
-- Description: 4-to-16 line decoder with active high/low selection
--              Enable input for cascading, active level configuration
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity Decoder4to16 is
    port (
        sel         : in  bit_vector(3 downto 0);
        enable      : in  bit;
        active_low  : in  bit;  -- '1' for active low outputs, '0' for active high
        outputs     : out bit_vector(15 downto 0)
    );
end Decoder4to16;

architecture Behavioral of Decoder4to16 is
    signal decoded : bit_vector(15 downto 0);
    signal enabled_out : bit_vector(15 downto 0);
begin
    -- Decode selection
    decode_process: process(sel)
    begin
        -- Initialize all outputs to '0'
        decoded <= (others => '0');

        case sel is
            when "0000" => decoded(0)  <= '1';
            when "0001" => decoded(1)  <= '1';
            when "0010" => decoded(2)  <= '1';
            when "0011" => decoded(3)  <= '1';
            when "0100" => decoded(4)  <= '1';
            when "0101" => decoded(5)  <= '1';
            when "0110" => decoded(6)  <= '1';
            when "0111" => decoded(7)  <= '1';
            when "1000" => decoded(8)  <= '1';
            when "1001" => decoded(9)  <= '1';
            when "1010" => decoded(10) <= '1';
            when "1011" => decoded(11) <= '1';
            when "1100" => decoded(12) <= '1';
            when "1101" => decoded(13) <= '1';
            when "1110" => decoded(14) <= '1';
            when "1111" => decoded(15) <= '1';
            when others => decoded <= (others => '0');
        end case;
    end process decode_process;

    -- Apply enable
    enable_gen: for i in 0 to 15 generate
        enabled_out(i) <= decoded(i) and enable;
    end generate enable_gen;

    -- Apply active level selection
    output_gen: for i in 0 to 15 generate
        outputs(i) <= enabled_out(i) xor active_low;
    end generate output_gen;

end Behavioral;

-- ============================================================================
-- 2-to-4 Decoder (Basic building block)
-- ============================================================================
entity Decoder2to4 is
    port (
        sel     : in  bit_vector(1 downto 0);
        enable  : in  bit;
        outputs : out bit_vector(3 downto 0)
    );
end Decoder2to4;

architecture Behavioral of Decoder2to4 is
begin
    process(sel, enable)
    begin
        outputs <= (others => '0');
        if enable = '1' then
            case sel is
                when "00" => outputs(0) <= '1';
                when "01" => outputs(1) <= '1';
                when "10" => outputs(2) <= '1';
                when "11" => outputs(3) <= '1';
                when others => outputs <= (others => '0');
            end case;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- 3-to-8 Decoder
-- ============================================================================
entity Decoder3to8 is
    port (
        sel     : in  bit_vector(2 downto 0);
        enable  : in  bit;
        outputs : out bit_vector(7 downto 0)
    );
end Decoder3to8;

architecture Behavioral of Decoder3to8 is
begin
    process(sel, enable)
    begin
        outputs <= (others => '0');
        if enable = '1' then
            case sel is
                when "000" => outputs(0) <= '1';
                when "001" => outputs(1) <= '1';
                when "010" => outputs(2) <= '1';
                when "011" => outputs(3) <= '1';
                when "100" => outputs(4) <= '1';
                when "101" => outputs(5) <= '1';
                when "110" => outputs(6) <= '1';
                when "111" => outputs(7) <= '1';
                when others => outputs <= (others => '0');
            end case;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Hierarchical 4-to-16 Decoder using 2-to-4 decoders
-- ============================================================================
entity Decoder4to16_Hierarchical is
    port (
        sel     : in  bit_vector(3 downto 0);
        enable  : in  bit;
        outputs : out bit_vector(15 downto 0)
    );
end Decoder4to16_Hierarchical;

architecture Structural of Decoder4to16_Hierarchical is
    signal group_enable : bit_vector(3 downto 0);
begin
    -- First level: decode upper 2 bits to select which group
    group_decoder: entity work.Decoder2to4
        port map (
            sel     => sel(3 downto 2),
            enable  => enable,
            outputs => group_enable
        );

    -- Second level: 4 decoders for lower 2 bits, each enabled by group
    dec_group0: entity work.Decoder2to4
        port map (
            sel     => sel(1 downto 0),
            enable  => group_enable(0),
            outputs => outputs(3 downto 0)
        );

    dec_group1: entity work.Decoder2to4
        port map (
            sel     => sel(1 downto 0),
            enable  => group_enable(1),
            outputs => outputs(7 downto 4)
        );

    dec_group2: entity work.Decoder2to4
        port map (
            sel     => sel(1 downto 0),
            enable  => group_enable(2),
            outputs => outputs(11 downto 8)
        );

    dec_group3: entity work.Decoder2to4
        port map (
            sel     => sel(1 downto 0),
            enable  => group_enable(3),
            outputs => outputs(15 downto 12)
        );
end Structural;

-- ============================================================================
-- BCD to Decimal Decoder (special case)
-- ============================================================================
entity BCDtoDecimal is
    port (
        bcd     : in  bit_vector(3 downto 0);
        enable  : in  bit;
        decimal : out bit_vector(9 downto 0);
        invalid : out bit  -- High if input > 9
    );
end BCDtoDecimal;

architecture Behavioral of BCDtoDecimal is
begin
    process(bcd, enable)
    begin
        decimal <= (others => '0');
        invalid <= '0';

        if enable = '1' then
            case bcd is
                when "0000" => decimal(0) <= '1';
                when "0001" => decimal(1) <= '1';
                when "0010" => decimal(2) <= '1';
                when "0011" => decimal(3) <= '1';
                when "0100" => decimal(4) <= '1';
                when "0101" => decimal(5) <= '1';
                when "0110" => decimal(6) <= '1';
                when "0111" => decimal(7) <= '1';
                when "1000" => decimal(8) <= '1';
                when "1001" => decimal(9) <= '1';
                when others =>
                    decimal <= (others => '0');
                    invalid <= '1';
            end case;
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Testbench
-- ============================================================================
entity Decoder_TB is
end Decoder_TB;

architecture Test of Decoder_TB is
    signal tb_sel        : bit_vector(3 downto 0);
    signal tb_enable     : bit;
    signal tb_active_low : bit;
    signal tb_outputs    : bit_vector(15 downto 0);
begin
    UUT: entity work.Decoder4to16
        port map (
            sel        => tb_sel,
            enable     => tb_enable,
            active_low => tb_active_low,
            outputs    => tb_outputs
        );

    stimulus: process
    begin
        tb_enable <= '1';
        tb_active_low <= '0';

        -- Test all 16 outputs
        for i in 0 to 15 loop
            tb_sel(0) <= '1' when (i mod 2) = 1 else '0';
            tb_sel(1) <= '1' when (i mod 4) >= 2 else '0';
            tb_sel(2) <= '1' when (i mod 8) >= 4 else '0';
            tb_sel(3) <= '1' when i >= 8 else '0';
            wait for 10 ns;
        end loop;

        -- Test disable
        tb_enable <= '0';
        tb_sel <= "0101";
        wait for 10 ns;

        -- Test active low
        tb_enable <= '1';
        tb_active_low <= '1';
        tb_sel <= "1010";
        wait for 10 ns;

        wait;
    end process stimulus;
end Test;
