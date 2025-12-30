CS2 Simple Vote provides all of the basic map voting functionality at a fraction of the resources.

Commands are:
rtv - to RockTheVote prior to a map vote. Cannot be initiated during warmup or after a map vote.
nominate - to nominate a map to be included in the standard map vote.
nextmap - to display the next map following a map vote.
revote - to revote within the standard map vote window.

The CS2SimpleVote.json config is auto-created on first launch, and is laid out as follows:

{

  "steam_api_key": "YOURAPIKEY", //The API key the plugin uses to fetch map names  
  "collection_id": "COLLECTION_ID", //The collection that is passed to the API to fetch map names from  
  "vote_round": 3, //Which round the map vote initiates on. Will persist for the entire round.  
  "rtv_percentage": 0.6, //The percentage (as a decimal) of votes required to initiate an RTV vote.  
  "rtv_change_delay": 5, //The delay in seconds before a completed RTV vote will change the map.  
  "vote_options_count": 8, //The amount of map options in a standard vote or RTV vote.  
  "nominate_per_page": 8, //The amount of maps displayed on a nominate screen per page.  
  "vote_reminder_enabled": true, //Reprint the map list on an interval to remind the player to vote if they haven't.  
  "vote_reminder_interval": 30, //The map list reminder interval.  
  "enable_rtv": true, //Enable RTV  
  "enable_nominate": true, //Enable Nominate  
  "ConfigVersion": 1  
}

