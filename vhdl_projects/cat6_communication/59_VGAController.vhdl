-- ============================================================================
-- VHDL Project: VGAController
-- Category: Communication Protocols
-- Complexity: High
-- Description: VGA timing generator with framebuffer interface
--              640x480 @ 60Hz, 25.175 MHz pixel clock
-- No IEEE libraries - raw VHDL implementation
-- ============================================================================

entity VGAController is
    port (
        clk_pixel   : in  bit;  -- 25.175 MHz
        reset       : in  bit;
        -- VGA output
        hsync       : out bit;
        vsync       : out bit;
        red         : out bit_vector(3 downto 0);
        green       : out bit_vector(3 downto 0);
        blue        : out bit_vector(3 downto 0);
        -- Framebuffer interface
        fb_addr     : out bit_vector(18 downto 0);  -- 640*480 = 307200
        fb_data     : in  bit_vector(11 downto 0);   -- 12-bit color
        fb_read     : out bit;
        -- Position
        pixel_x     : out bit_vector(9 downto 0);
        pixel_y     : out bit_vector(9 downto 0);
        active      : out bit
    );
end VGAController;

architecture Behavioral of VGAController is
    -- 640x480 @ 60Hz timing
    constant H_ACTIVE : integer := 640;
    constant H_FP : integer := 16;
    constant H_SYNC : integer := 96;
    constant H_BP : integer := 48;
    constant H_TOTAL : integer := 800;

    constant V_ACTIVE : integer := 480;
    constant V_FP : integer := 10;
    constant V_SYNC : integer := 2;
    constant V_BP : integer := 33;
    constant V_TOTAL : integer := 525;

    signal h_count : integer range 0 to H_TOTAL-1;
    signal v_count : integer range 0 to V_TOTAL-1;
    signal h_active, v_active : bit;

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
    -- Horizontal counter
    h_counter: process(clk_pixel, reset)
    begin
        if reset = '1' then
            h_count <= 0;
        elsif clk_pixel'event and clk_pixel = '1' then
            if h_count < H_TOTAL - 1 then
                h_count <= h_count + 1;
            else
                h_count <= 0;
            end if;
        end if;
    end process;

    -- Vertical counter
    v_counter: process(clk_pixel, reset)
    begin
        if reset = '1' then
            v_count <= 0;
        elsif clk_pixel'event and clk_pixel = '1' then
            if h_count = H_TOTAL - 1 then
                if v_count < V_TOTAL - 1 then
                    v_count <= v_count + 1;
                else
                    v_count <= 0;
                end if;
            end if;
        end if;
    end process;

    -- Sync signals (active low)
    hsync <= '0' when h_count >= H_ACTIVE + H_FP and
                     h_count < H_ACTIVE + H_FP + H_SYNC else '1';
    vsync <= '0' when v_count >= V_ACTIVE + V_FP and
                     v_count < V_ACTIVE + V_FP + V_SYNC else '1';

    -- Active region
    h_active <= '1' when h_count < H_ACTIVE else '0';
    v_active <= '1' when v_count < V_ACTIVE else '0';
    active <= h_active and v_active;

    -- Position outputs
    pixel_x <= to_bv(h_count, 10) when h_count < H_ACTIVE else (others => '0');
    pixel_y <= to_bv(v_count, 10) when v_count < V_ACTIVE else (others => '0');

    -- Framebuffer address
    fb_addr <= to_bv(v_count * H_ACTIVE + h_count, 19)
               when h_active = '1' and v_active = '1' else (others => '0');
    fb_read <= h_active and v_active;

    -- Color output
    process(h_active, v_active, fb_data)
    begin
        if h_active = '1' and v_active = '1' then
            red <= fb_data(11 downto 8);
            green <= fb_data(7 downto 4);
            blue <= fb_data(3 downto 0);
        else
            red <= "0000";
            green <= "0000";
            blue <= "0000";
        end if;
    end process;
end Behavioral;

-- ============================================================================
-- Pattern Generator (test pattern)
-- ============================================================================
entity VGAPatternGen is
    port (
        clk_pixel : in bit;
        reset : in bit;
        pattern_sel : in bit_vector(2 downto 0);
        pixel_x : in bit_vector(9 downto 0);
        pixel_y : in bit_vector(9 downto 0);
        active : in bit;
        color_out : out bit_vector(11 downto 0)
    );
end VGAPatternGen;

architecture Behavioral of VGAPatternGen is
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
begin
    process(pixel_x, pixel_y, pattern_sel, active)
        variable x, y : integer;
    begin
        x := to_int(pixel_x);
        y := to_int(pixel_y);
        color_out <= (others => '0');

        if active = '1' then
            case pattern_sel is
                when "000" =>  -- Color bars
                    if x < 80 then color_out <= "111111111111";
                    elsif x < 160 then color_out <= "111111110000";
                    elsif x < 240 then color_out <= "000011111111";
                    elsif x < 320 then color_out <= "000011110000";
                    elsif x < 400 then color_out <= "111100001111";
                    elsif x < 480 then color_out <= "111100000000";
                    elsif x < 560 then color_out <= "000000001111";
                    else color_out <= "000000000000";
                    end if;
                when "001" =>  -- Gradient
                    color_out <= pixel_x(9 downto 6) & pixel_y(9 downto 6) & "0000";
                when "010" =>  -- Checkerboard
                    if ((x / 32) mod 2) = ((y / 32) mod 2) then
                        color_out <= "111111111111";
                    else
                        color_out <= "000000000000";
                    end if;
                when "011" =>  -- Grid
                    if (x mod 64) = 0 or (y mod 64) = 0 then
                        color_out <= "111111111111";
                    end if;
                when others =>
                    color_out <= "010001000100";
            end case;
        end if;
    end process;
end Behavioral;
