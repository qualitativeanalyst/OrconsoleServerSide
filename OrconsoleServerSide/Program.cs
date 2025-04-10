using System;
using System.Collections.Generic;
using System.Threading;
using tt_net_sdk;

namespace OrconsoleServerSide
{
    class Program
    {
        // Declare SDK objects
        private static TTAPI m_api = null;
        private static WorkerDispatcher m_disp = null;
        private static bool m_shutdownRequested = false;
        private static object m_lock = new object();
        private static Dictionary<string, InstrumentLookupSubscription> m_instrumentLookups = new Dictionary<string, InstrumentLookupSubscription>();
        private static Dictionary<string, PriceSubscription> m_priceSubscriptions = new Dictionary<string, PriceSubscription>();

        static void Main(string[] args)
        {
            Console.WriteLine("TT .NET SDK Server Side application starting...");

            // Initialize the TT API
            ApiInitializeHandler initHandler = new ApiInitializeHandler(TTAPIInitHandler);
            
            try
            {
                // Create a dispatcher for events
                m_disp = WorkerDispatcher.AttachWorkerDispatcher();
                
                // Create TTAPIOptions with the correct constructor
                // Replace "Your_App_Secret_Key_Here" with your actual App Secret Key
                TTAPIOptions apiOptions = new TTAPIOptions(
                    TTAPIOptions.SDKMode.Server,
                    ServiceEnvironment.ProdSim, // Change as needed
                    "Your_App_Secret_Key_Here", 
                    30000); // 30 seconds timeout
                
                // Create the TT API instance
                TTAPI.CreateTTAPI(m_disp, apiOptions, initHandler);
                
                Console.WriteLine("TT API initialization started...");

                Console.WriteLine("Press <Enter> to shut down");
                Console.ReadLine();
                
                lock (m_lock)
                {
                    m_shutdownRequested = true;
                }

                Console.WriteLine("Shutting down...");

                // Clean up subscriptions
                CleanupSubscriptions();

                // We can't directly call Shutdown() from a non-static context
                if (m_api != null)
                {
                    // For older SDK versions that don't have Dispose()
                    try
                    {
                        m_disp.DispatchAction(() => {
                            // Attempt to shutdown the API using reflection if needed
                            var shutdownMethod = typeof(TTAPI).GetMethod("Shutdown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (shutdownMethod != null)
                            {
                                shutdownMethod.Invoke(m_api, null);
                            }
                            m_api = null;
                        });
                    }
                    catch
                    {
                        // Ignore any exceptions during shutdown
                        Console.WriteLine("Could not call shutdown method, continuing with cleanup");
                    }
                }

                // Wait a moment for shutdown to complete
                Thread.Sleep(2000);
                m_disp.Shutdown();

                Console.WriteLine("TT API has been shut down");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing TT API: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
            }
        }

        private static void TTAPIInitHandler(TTAPI api, ApiCreationException ex)
        {
            if (ex == null)
            {
                Console.WriteLine("TT API Initialization Successful");
                
                // Save the API instance
                m_api = api;
                
                // Register for status updates
                api.TTAPIStatusUpdate += Api_TTAPIStatusUpdate;
                
                // Dispatch actions via the worker
                m_disp.DispatchAction(() => {
                    // Create a dedicated thread to lookup and subscribe to instruments
                    Thread workerThread = new Thread(InitializeInstruments);
                    workerThread.Name = "TT Worker Thread";
                    workerThread.Start();
                });
            }
            else
            {
                Console.WriteLine($"TT API Initialization Failed: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
            }
        }

        private static void Api_TTAPIStatusUpdate(object sender, TTAPIStatusUpdateEventArgs e)
        {
            Console.WriteLine($"TT API Status Update: {DateTime.Now}");
        }

        private static void InitializeInstruments()
        {
            // Wait for the API to be initialized
            while (m_api == null)
            {
                Thread.Sleep(100);
                
                lock (m_lock)
                {
                    if (m_shutdownRequested)
                        return;
                }
            }

            try
            {
                // Look up instrument by ID (as requested)
                LookupInstrumentById(3099881435134817296);
                
                // Also look up some instruments by symbol for demonstration
                LookupInstrumentByComponents("CME", "ES", "Jun24");
                LookupInstrumentByComponents("EUREX", "FDAX", "Jun24");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error initializing instruments: {ex.Message}");
            }
            
            // Continue running until shutdown is requested
            while (true)
            {
                Thread.Sleep(1000);
                
                lock (m_lock)
                {
                    if (m_shutdownRequested)
                        break;
                }
            }
        }

        private static void LookupInstrumentById(ulong instrumentId)
        {
            try
            {
                Console.WriteLine($"Looking up instrument by ID: {instrumentId}");
                
                // Create an instrument lookup request by ID
                InstrumentLookupSubscription instrLookup = new InstrumentLookupSubscription(m_disp, new InstrumentKey(instrumentId), 
                    InstrumentLookupType.ByKey);
                
                instrLookup.Update += InstrLookup_Update;
                
                // Save the lookup for cleanup
                m_instrumentLookups["ID_" + instrumentId] = instrLookup;
                
                // Request the lookup
                instrLookup.Start();
                Console.WriteLine($"Lookup request sent for instrument ID {instrumentId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error looking up instrument by ID: {ex.Message}");
            }
        }

        private static void LookupInstrumentByComponents(string market, string product, string contract)
        {
            try
            {
                string key = $"{market}.{product}.{contract}";
                Console.WriteLine($"Looking up instrument: {key}");
                
                // Create instrument key using components
                InstrumentKey instrumentKey = new InstrumentKey(new MarketKey(market), new ProductKey(product), new ContractKey(contract));
                
                // Create an instrument lookup request
                InstrumentLookupSubscription instrLookup = new InstrumentLookupSubscription(
                    m_disp, 
                    instrumentKey, 
                    InstrumentLookupType.ByKey);
                
                instrLookup.Update += InstrLookup_Update;
                
                // Save the lookup for cleanup
                m_instrumentLookups[key] = instrLookup;
                
                // Request the lookup
                instrLookup.Start();
                Console.WriteLine($"Lookup request sent for {key}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error subscribing to instrument: {ex.Message}");
            }
        }

        private static void InstrLookup_Update(object sender, InstrumentLookupEventArgs e)
        {
            try
            {
                if (e.Instrument != null && e.Error == null)
                {
                    Instrument instrument = e.Instrument;
                    
                    // Get instrument details
                    string market = instrument.InstrumentDetails.BloombergExchangeCode;
                    string product = instrument.Product.Name;
                    string contract = instrument.Name;
                    string key = $"{market}.{product}.{contract}";
                    ulong instrumentId = instrument.Key.InstrumentId;
                    
                    Console.WriteLine($"Instrument found: {key} (ID: {instrumentId})");
                    Console.WriteLine($"Instrument details:");
                    Console.WriteLine($"  Name: {instrument.Name}");
                    Console.WriteLine($"  Product Type: {instrument.Product.Type}");
                    
                    // Subscribe to market data
                    SubscribeToPriceUpdates(instrument);
                }
                else if (e.Error != null)
                {
                    Console.WriteLine($"Error looking up instrument: {e.Error.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in instrument lookup handler: {ex.Message}");
            }
        }

        private static void SubscribeToPriceUpdates(Instrument instrument)
        {
            try
            {
                string market = instrument.Market.Name;
                string product = instrument.Product.Name;
                string contract = instrument.Contract.Name;
                string key = $"{market}.{product}.{contract}";
                
                // Subscribe to market data
                PriceSubscription priceSub = new PriceSubscription(instrument, m_disp);
                
                // Set up the subscription settings
                priceSub.Settings = new PriceSubscriptionSettings(PriceSubscriptionType.InsideMarket);
                
                priceSub.Update += PriceSub_Update;
                
                // Save the subscription for cleanup
                m_priceSubscriptions[key] = priceSub;
                
                // Start the subscription
                priceSub.Start();
                Console.WriteLine($"Price subscription started for {key}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error subscribing to price updates: {ex.Message}");
            }
        }

        private static void PriceSub_Update(object sender, PriceUpdateEventArgs e)
        {
            try
            {
                if (e.Error == null)
                {
                    Instrument instrument = e.Instrument;
                    string market = instrument.Market.Name;
                    string product = instrument.Product.Name;
                    string contract = instrument.Contract.Name;
                    
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} - {market}.{product}.{contract}");
                    
                    if (e.PriceSubscriptionFields != null)
                    {
                        // Access price data
                        double bidPrice = 0, askPrice = 0, lastPrice = 0;
                        long bidQty = 0, askQty = 0, lastQty = 0;
                        bool hasBid = false, hasAsk = false, hasLast = false;
                        
                        try { hasBid = e.PriceSubscriptionFields.HasBidPrice; bidPrice = e.PriceSubscriptionFields.BidPrice; bidQty = e.PriceSubscriptionFields.BidQuantity; } catch { }
                        try { hasAsk = e.PriceSubscriptionFields.HasAskPrice; askPrice = e.PriceSubscriptionFields.AskPrice; askQty = e.PriceSubscriptionFields.AskQuantity; } catch { }
                        try { hasLast = e.PriceSubscriptionFields.HasLastTradedPrice; lastPrice = e.PriceSubscriptionFields.LastTradedPrice; lastQty = e.PriceSubscriptionFields.LastTradedQuantity; } catch { }
                        
                        // Display price information
                        if (hasBid)
                            Console.WriteLine($"  Bid: {bidPrice} x {bidQty}");
                        
                        if (hasAsk)
                            Console.WriteLine($"  Ask: {askPrice} x {askQty}");
                        
                        if (hasLast)
                            Console.WriteLine($"  Last: {lastPrice} x {lastQty}");
                        
                        Console.WriteLine();
                    }
                }
                else
                {
                    Console.WriteLine($"Price update error: {e.Error.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in price update handler: {ex.Message}");
            }
        }

        private static void CleanupSubscriptions()
        {
            // Stop and dispose of all price subscriptions
            foreach (var pair in m_priceSubscriptions)
            {
                try
                {
                    PriceSubscription sub = pair.Value;
                    sub.Start -= PriceSub_Update;
                    
                    // Clean shutdown
                    sub.Stop();
                    
                    Console.WriteLine($"Stopped price subscription for {pair.Key}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping price subscription: {ex.Message}");
                }
            }
            m_priceSubscriptions.Clear();
            
            // Clean up all instrument lookups
            foreach (var pair in m_instrumentLookups)
            {
                try
                {
                    InstrumentLookupSubscription lookup = pair.Value;
                    lookup.Update -= InstrLookup_Update;
                    
                    lookup.Stop();
                    Console.WriteLine($"Cleaned up instrument lookup for {pair.Key}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error cleaning up instrument lookup: {ex.Message}");
                }
            }
            m_instrumentLookups.Clear();
        }
    }
}