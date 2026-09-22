-- ============================================================================
-- VHDL Project: ClockCrossingSync
-- Category: Timing & Synchronization
-- Complexity: High
-- Description: Clock Domain Crossing (CDC) synchronizers
--              Includes: 2FF sync, pulse sync, handshake, gray code FIFO ptr
-- Uses IEEE for std_logic (required for proper CDC)
-- ============================================================================

library IEEE;
use IEEE.std_logic_1164.all;

-- ============================================================================
-- Basic 2-FF Synchronizer
-- ============================================================================
entity Sync2FF is
    generic (
        INIT_VALUE : std_logic := '0';
        NUM_STAGES : integer := 2
    );
    port (
        clk_dest    : in  std_logic;
        reset       : in  std_logic;
        data_in     : in  std_logic;
        data_out    : out std_logic
    );
end Sync2FF;

architecture Behavioral of Sync2FF is
    signal sync_chain : std_logic_vector(NUM_STAGES-1 downto 0);
    attribute ASYNC_REG : string;
    attribute ASYNC_REG of sync_chain : signal is "TRUE";
begin
    process(clk_dest, reset)
    begin
        if reset = '1' then
            sync_chain <= (others => INIT_VALUE);
        elsif rising_edge(clk_dest) then
            sync_chain <= sync_chain(NUM_STAGES-2 downto 0) & data_in;
        end if;
    end process;

    data_out <= sync_chain(NUM_STAGES-1);
end Behavioral;

-- ============================================================================
-- Pulse Synchronizer (transfers single-cycle pulse across domains)
-- ============================================================================
entity PulseSynchronizer is
    port (
        -- Source domain
        clk_src     : in  std_logic;
        reset_src   : in  std_logic;
        pulse_in    : in  std_logic;
        busy        : out std_logic;
        -- Destination domain
        clk_dest    : in  std_logic;
        reset_dest  : in  std_logic;
        pulse_out   : out std_logic
    );
end PulseSynchronizer;

architecture Behavioral of PulseSynchronizer is
    -- Source domain signals
    signal toggle_src : std_logic;

    -- Destination domain signals
    signal toggle_sync1, toggle_sync2, toggle_sync3 : std_logic;

    -- Feedback for busy
    signal ack_sync1, ack_sync2 : std_logic;

    attribute ASYNC_REG : string;
    attribute ASYNC_REG of toggle_sync1, toggle_sync2 : signal is "TRUE";
    attribute ASYNC_REG of ack_sync1, ack_sync2 : signal is "TRUE";
begin
    -- Source domain: toggle on pulse
    src_proc: process(clk_src, reset_src)
    begin
        if reset_src = '1' then
            toggle_src <= '0';
        elsif rising_edge(clk_src) then
            if pulse_in = '1' then
                toggle_src <= not toggle_src;
            end if;
        end if;
    end process src_proc;

    -- Destination domain: synchronize and edge detect
    dest_proc: process(clk_dest, reset_dest)
    begin
        if reset_dest = '1' then
            toggle_sync1 <= '0';
            toggle_sync2 <= '0';
            toggle_sync3 <= '0';
        elsif rising_edge(clk_dest) then
            toggle_sync1 <= toggle_src;
            toggle_sync2 <= toggle_sync1;
            toggle_sync3 <= toggle_sync2;
        end if;
    end process dest_proc;

    -- Pulse on edge of synchronized toggle
    pulse_out <= toggle_sync2 xor toggle_sync3;

    -- Feedback to source for busy indication
    ack_proc: process(clk_src, reset_src)
    begin
        if reset_src = '1' then
            ack_sync1 <= '0';
            ack_sync2 <= '0';
        elsif rising_edge(clk_src) then
            ack_sync1 <= toggle_sync2;
            ack_sync2 <= ack_sync1;
        end if;
    end process ack_proc;

    -- Busy when toggle hasn't been acknowledged
    busy <= toggle_src xor ack_sync2;

end Behavioral;

-- ============================================================================
-- Handshake Synchronizer (for multi-bit data transfer)
-- ============================================================================
entity HandshakeSynchronizer is
    generic (
        DATA_WIDTH : integer := 8
    );
    port (
        -- Source domain
        clk_src     : in  std_logic;
        reset_src   : in  std_logic;
        data_in     : in  std_logic_vector(DATA_WIDTH-1 downto 0);
        valid_in    : in  std_logic;
        ready_out   : out std_logic;
        -- Destination domain
        clk_dest    : in  std_logic;
        reset_dest  : in  std_logic;
        data_out    : out std_logic_vector(DATA_WIDTH-1 downto 0);
        valid_out   : out std_logic;
        ack_in      : in  std_logic
    );
end HandshakeSynchronizer;

architecture Behavioral of HandshakeSynchronizer is
    type src_state_type is (IDLE, WAIT_ACK);
    type dest_state_type is (IDLE, DATA_VALID, WAIT_DEASSERT);

    signal src_state : src_state_type;
    signal dest_state : dest_state_type;

    -- Request signal (src -> dest)
    signal req_src : std_logic;
    signal req_sync1, req_sync2 : std_logic;

    -- Acknowledge signal (dest -> src)
    signal ack_dest : std_logic;
    signal ack_sync1, ack_sync2 : std_logic;

    -- Data holding register
    signal data_reg : std_logic_vector(DATA_WIDTH-1 downto 0);

    attribute ASYNC_REG : string;
    attribute ASYNC_REG of req_sync1, req_sync2 : signal is "TRUE";
    attribute ASYNC_REG of ack_sync1, ack_sync2 : signal is "TRUE";
begin
    -- Source domain state machine
    src_proc: process(clk_src, reset_src)
    begin
        if reset_src = '1' then
            src_state <= IDLE;
            req_src <= '0';
            data_reg <= (others => '0');
            ready_out <= '1';
        elsif rising_edge(clk_src) then
            case src_state is
                when IDLE =>
                    ready_out <= '1';
                    if valid_in = '1' then
                        data_reg <= data_in;
                        req_src <= '1';
                        ready_out <= '0';
                        src_state <= WAIT_ACK;
                    end if;

                when WAIT_ACK =>
                    if ack_sync2 = '1' then
                        req_src <= '0';
                        src_state <= IDLE;
                    end if;
            end case;
        end if;
    end process src_proc;

    -- Synchronize ACK to source domain
    ack_sync_proc: process(clk_src, reset_src)
    begin
        if reset_src = '1' then
            ack_sync1 <= '0';
            ack_sync2 <= '0';
        elsif rising_edge(clk_src) then
            ack_sync1 <= ack_dest;
            ack_sync2 <= ack_sync1;
        end if;
    end process ack_sync_proc;

    -- Synchronize REQ to destination domain
    req_sync_proc: process(clk_dest, reset_dest)
    begin
        if reset_dest = '1' then
            req_sync1 <= '0';
            req_sync2 <= '0';
        elsif rising_edge(clk_dest) then
            req_sync1 <= req_src;
            req_sync2 <= req_sync1;
        end if;
    end process req_sync_proc;

    -- Destination domain state machine
    dest_proc: process(clk_dest, reset_dest)
    begin
        if reset_dest = '1' then
            dest_state <= IDLE;
            ack_dest <= '0';
            valid_out <= '0';
            data_out <= (others => '0');
        elsif rising_edge(clk_dest) then
            case dest_state is
                when IDLE =>
                    valid_out <= '0';
                    if req_sync2 = '1' then
                        data_out <= data_reg;  -- Sample data (stable due to handshake)
                        valid_out <= '1';
                        dest_state <= DATA_VALID;
                    end if;

                when DATA_VALID =>
                    if ack_in = '1' then
                        ack_dest <= '1';
                        valid_out <= '0';
                        dest_state <= WAIT_DEASSERT;
                    end if;

                when WAIT_DEASSERT =>
                    if req_sync2 = '0' then
                        ack_dest <= '0';
                        dest_state <= IDLE;
                    end if;
            end case;
        end if;
    end process dest_proc;

end Behavioral;

-- ============================================================================
-- Gray Code Pointer Synchronizer (for async FIFO)
-- ============================================================================
entity GrayPtrSync is
    generic (
        PTR_WIDTH : integer := 4
    );
    port (
        clk_dest    : in  std_logic;
        reset       : in  std_logic;
        gray_ptr_in : in  std_logic_vector(PTR_WIDTH-1 downto 0);
        gray_ptr_out: out std_logic_vector(PTR_WIDTH-1 downto 0)
    );
end GrayPtrSync;

architecture Behavioral of GrayPtrSync is
    signal sync1, sync2 : std_logic_vector(PTR_WIDTH-1 downto 0);
    attribute ASYNC_REG : string;
    attribute ASYNC_REG of sync1, sync2 : signal is "TRUE";
begin
    process(clk_dest, reset)
    begin
        if reset = '1' then
            sync1 <= (others => '0');
            sync2 <= (others => '0');
        elsif rising_edge(clk_dest) then
            sync1 <= gray_ptr_in;
            sync2 <= sync1;
        end if;
    end process;

    gray_ptr_out <= sync2;
end Behavioral;

-- ============================================================================
-- Reset Synchronizer
-- ============================================================================
entity ResetSynchronizer is
    generic (
        NUM_STAGES : integer := 3;
        RESET_ACTIVE : std_logic := '1'
    );
    port (
        clk         : in  std_logic;
        async_reset : in  std_logic;
        sync_reset  : out std_logic
    );
end ResetSynchronizer;

architecture Behavioral of ResetSynchronizer is
    signal reset_chain : std_logic_vector(NUM_STAGES-1 downto 0);
    attribute ASYNC_REG : string;
    attribute ASYNC_REG of reset_chain : signal is "TRUE";
begin
    process(clk, async_reset)
    begin
        if async_reset = RESET_ACTIVE then
            reset_chain <= (others => RESET_ACTIVE);
        elsif rising_edge(clk) then
            reset_chain <= reset_chain(NUM_STAGES-2 downto 0) & (not RESET_ACTIVE);
        end if;
    end process;

    sync_reset <= reset_chain(NUM_STAGES-1);
end Behavioral;

-- ============================================================================
-- Bus Synchronizer (for slow-changing multi-bit signals)
-- ============================================================================
entity BusSynchronizer is
    generic (
        DATA_WIDTH : integer := 8;
        NUM_STAGES : integer := 2
    );
    port (
        clk_dest    : in  std_logic;
        reset       : in  std_logic;
        data_in     : in  std_logic_vector(DATA_WIDTH-1 downto 0);
        data_out    : out std_logic_vector(DATA_WIDTH-1 downto 0)
    );
end BusSynchronizer;

architecture Behavioral of BusSynchronizer is
    type sync_array is array (0 to NUM_STAGES-1) of std_logic_vector(DATA_WIDTH-1 downto 0);
    signal sync_regs : sync_array;
begin
    process(clk_dest, reset)
    begin
        if reset = '1' then
            for i in 0 to NUM_STAGES-1 loop
                sync_regs(i) <= (others => '0');
            end loop;
        elsif rising_edge(clk_dest) then
            sync_regs(0) <= data_in;
            for i in 1 to NUM_STAGES-1 loop
                sync_regs(i) <= sync_regs(i-1);
            end loop;
        end if;
    end process;

    data_out <= sync_regs(NUM_STAGES-1);
end Behavioral;

-- ============================================================================
-- Level Synchronizer with Edge Detection
-- ============================================================================
entity LevelSyncWithEdge is
    port (
        clk_dest    : in  std_logic;
        reset       : in  std_logic;
        level_in    : in  std_logic;
        level_out   : out std_logic;
        rising_edge_out : out std_logic;
        falling_edge_out: out std_logic
    );
end LevelSyncWithEdge;

architecture Behavioral of LevelSyncWithEdge is
    signal sync1, sync2, sync3 : std_logic;
    attribute ASYNC_REG : string;
    attribute ASYNC_REG of sync1, sync2 : signal is "TRUE";
begin
    process(clk_dest, reset)
    begin
        if reset = '1' then
            sync1 <= '0';
            sync2 <= '0';
            sync3 <= '0';
        elsif rising_edge(clk_dest) then
            sync1 <= level_in;
            sync2 <= sync1;
            sync3 <= sync2;
        end if;
    end process;

    level_out <= sync2;
    rising_edge_out <= sync2 and (not sync3);
    falling_edge_out <= (not sync2) and sync3;
end Behavioral;
