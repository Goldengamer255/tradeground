using System.Text.Json;
using System.Text.Json.Serialization;
using Tradeground.Engine;

namespace Tradeground.Contracts;

/// <summary>
/// Compile-time JSON code for every shape the browser app sends or receives.
/// </summary>
/// <remarks>
/// Normally System.Text.Json discovers a type's properties by reflection at runtime. The browser build
/// trims unused code to keep the download small, and trimming can remove what reflection needs. A
/// source-generated context writes the (de)serialisation code at compile time instead: nothing to
/// trim away, and faster too. Enums travel as names ("Buy"), matching the API.
/// </remarks>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(ModeSummary[]))]
[JsonSerializable(typeof(CreateRoomRequest))]
[JsonSerializable(typeof(CreateRoomResponse))]
[JsonSerializable(typeof(JoinRoomResponse))]
[JsonSerializable(typeof(PlaceOrderRequest))]
[JsonSerializable(typeof(PlaceOrderResponse))]
[JsonSerializable(typeof(List<PublicTrade>))]
[JsonSerializable(typeof(AccountView))]
[JsonSerializable(typeof(RoomView))]
[JsonSerializable(typeof(ClueView))]
[JsonSerializable(typeof(SettlementView))]
[JsonSerializable(typeof(BookSnapshot))]
[JsonSerializable(typeof(TimeframeView[]))]
[JsonSerializable(typeof(TimeframeRequest))]
[JsonSerializable(typeof(CallRequest))]
[JsonSerializable(typeof(ChartStarted))]
[JsonSerializable(typeof(ChartView))]
[JsonSerializable(typeof(ChartHighlight[]))]
public sealed partial class TradegroundJsonContext : JsonSerializerContext;
